"""
JMComic Client - Python Backend Server
Flask REST API wrapping the jmcomic library
"""
import os
import base64
import ctypes
import ctypes.wintypes
import hmac
import json
import re
import secrets
import tempfile
import time
import logging
import threading
import traceback
from pathlib import Path
from functools import wraps

from flask import Flask, request, jsonify, send_file, Response, after_this_request
from werkzeug.exceptions import BadRequest, HTTPException

# ─── jmcomic imports ────────────────────────────────────────────
import jmcomic
from jmcomic import (
    JmOption, JmModuleConfig, JmMagicConstants,
    download_album, download_photo,
)

# ─── App setup ───────────────────────────────────────────────────
APP_NAME = "禁漫天堂"
# Use AppData\Roaming\禁漫天堂 for persistent storage
APP_DATA_DIR = Path(os.environ.get("APPDATA", str(Path.home()))) / APP_NAME
APP_DATA_DIR.mkdir(parents=True, exist_ok=True)

SETTINGS_FILE = APP_DATA_DIR / "settings.json"
CREDENTIALS_FILE = APP_DATA_DIR / "credentials.json"
DOWNLOAD_DIR = APP_DATA_DIR / "downloads"

DEFAULT_SETTINGS = {
    "download_dir": str(DOWNLOAD_DIR),
    "image_format": ".jpg",
    "output_format": "image",
    "client_impl": "api",
}

# The WPF client supplies this token through the process environment.  A
# random fallback keeps direct `python server.py` launches protected too;
# direct callers can inspect the process environment/logging when needed.
API_TOKEN = os.environ.get("JMCOMIC_API_TOKEN") or secrets.token_urlsafe(32)
_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]+$")
_ALLOWED_IMAGE_FORMATS = {".jpg", ".png", ".webp", ".gif"}
_IMAGE_SUFFIXES = _ALLOWED_IMAGE_FORMATS | {".jpeg"}
_ALLOWED_OUTPUT_FORMATS = {"image", "pdf"}

app = Flask(__name__)

logging.basicConfig(level=logging.INFO, format="[%(asctime)s] %(message)s")
log = logging.getLogger("jmcomic-backend")


@app.before_request
def authenticate_local_api():
    """Require the per-process token for every local API request."""
    supplied = request.headers.get("X-JMComic-Token", "")
    if not supplied or not hmac.compare_digest(supplied, API_TOKEN):
        return jsonify({"error": "未授权请求"}), 401
    return None

# ─── Global state ────────────────────────────────────────────────
_client = None
_client_lock = threading.Lock()
_download_jobs = {}  # job_id -> {status, progress, total, error, path}
_download_cancel_events = {}  # job_id -> threading.Event
_job_counter = 0
_job_lock = threading.Lock()


def _json_object():
    data = request.get_json(silent=True)
    if not isinstance(data, dict):
        raise BadRequest("请求体必须是 JSON 对象")
    return data


def _positive_int_arg(name, default):
    raw = request.args.get(name)
    if raw is None or raw == "":
        return default
    try:
        value = int(raw)
    except (TypeError, ValueError) as e:
        raise BadRequest(f"{name} 必须是正整数") from e
    if value < 1:
        raise BadRequest(f"{name} 必须是正整数")
    return value


def _validate_identifier(value, field_name):
    value = str(value or "")
    if not value or not _ID_PATTERN.fullmatch(value):
        raise BadRequest(f"{field_name} 格式无效")
    return value


def _validate_settings_update(settings):
    allowed = {"download_dir", "image_format", "output_format", "client_impl"}
    unknown = set(settings) - allowed
    if unknown:
        raise BadRequest(f"不支持的设置项: {', '.join(sorted(unknown))}")
    if "download_dir" in settings:
        if not isinstance(settings["download_dir"], str) or not settings["download_dir"].strip():
            raise BadRequest("download_dir 必须是非空字符串")
        settings["download_dir"] = _normalize_download_dir(settings["download_dir"])
    if "image_format" in settings:
        image_format = str(settings["image_format"]).lower()
        if image_format not in _ALLOWED_IMAGE_FORMATS:
            raise BadRequest("不支持的图片格式")
        settings["image_format"] = image_format
    if "output_format" in settings:
        output_format = str(settings["output_format"]).lower()
        if output_format not in _ALLOWED_OUTPUT_FORMATS:
            raise BadRequest("不支持的输出格式")
        settings["output_format"] = output_format
    if "client_impl" in settings:
        if not isinstance(settings["client_impl"], str) or not settings["client_impl"].strip():
            raise BadRequest("client_impl 必须是非空字符串")


def _image_files(root):
    root = Path(root)
    if not root.is_dir():
        return []
    result = []
    for path in root.rglob("*"):
        if path.is_file() and path.suffix.lower() in _IMAGE_SUFFIXES:
            try:
                if path.stat().st_size > 0:
                    result.append(path)
            except OSError:
                continue
    return sorted(result, key=lambda p: str(p).lower())


def _chapter_has_images(path):
    return bool(_image_files(path))


def _album_root_from_option(option, album, fallback):
    try:
        root = Path(option.dir_rule.decide_album_root_dir(album))
        if root.exists():
            return root
    except Exception:
        pass
    return Path(fallback)


class DownloadCancelled(Exception):
    pass

# ─── Settings helpers ────────────────────────────────────────────

def _normalize_download_dir(value):
    path = Path(os.path.expanduser(str(value)))
    if not path.is_absolute():
        path = APP_DATA_DIR / path
    return str(path.resolve())


def load_settings():
    settings = dict(DEFAULT_SETTINGS)
    if SETTINGS_FILE.exists():
        try:
            with open(SETTINGS_FILE, "r", encoding="utf-8") as f:
                saved = json.load(f)
            if isinstance(saved, dict):
                settings.update(saved)
            else:
                log.warning("Ignoring non-object settings file: %s", SETTINGS_FILE)
        except (OSError, json.JSONDecodeError) as e:
            log.warning("Ignoring unreadable settings file %s: %s", SETTINGS_FILE, e)
    if isinstance(settings.get("download_dir"), str):
        settings["download_dir"] = _normalize_download_dir(settings["download_dir"])
    return settings


def save_settings(settings):
    SETTINGS_FILE.parent.mkdir(parents=True, exist_ok=True)
    temp_path = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            dir=SETTINGS_FILE.parent,
            prefix="settings-",
            suffix=".tmp",
            delete=False,
        ) as f:
            temp_path = Path(f.name)
            json.dump(settings, f, ensure_ascii=False, indent=2)
            f.flush()
            os.fsync(f.fileno())
        os.replace(temp_path, SETTINGS_FILE)
    finally:
        if temp_path is not None and temp_path.exists():
            try:
                temp_path.unlink()
            except OSError:
                pass


class _DataBlob(ctypes.Structure):
    _fields_ = [
        ("cbData", ctypes.wintypes.DWORD),
        ("pbData", ctypes.POINTER(ctypes.c_byte)),
    ]


def _protect_password(password):
    """Encrypt a password for the current Windows user with DPAPI."""
    if os.name != "nt":
        raise RuntimeError("Windows DPAPI is unavailable on this platform")

    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32
    raw = password.encode("utf-8")
    raw_buffer = ctypes.create_string_buffer(raw)
    input_blob = _DataBlob(
        len(raw),
        ctypes.cast(raw_buffer, ctypes.POINTER(ctypes.c_byte)),
    )
    output_blob = _DataBlob()
    if not crypt32.CryptProtectData(
        ctypes.byref(input_blob),
        None,
        None,
        None,
        None,
        0,
        ctypes.byref(output_blob),
    ):
        raise ctypes.WinError()
    try:
        protected = ctypes.string_at(output_blob.pbData, output_blob.cbData)
        return base64.b64encode(protected).decode("ascii")
    finally:
        kernel32.LocalFree(output_blob.pbData)


def _unprotect_password(encoded):
    """Decrypt a DPAPI value created by _protect_password."""
    if os.name != "nt":
        raise RuntimeError("Windows DPAPI is unavailable on this platform")

    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32
    protected = base64.b64decode(encoded.encode("ascii"))
    raw_buffer = ctypes.create_string_buffer(protected)
    input_blob = _DataBlob(
        len(protected),
        ctypes.cast(raw_buffer, ctypes.POINTER(ctypes.c_byte)),
    )
    output_blob = _DataBlob()
    if not crypt32.CryptUnprotectData(
        ctypes.byref(input_blob),
        None,
        None,
        None,
        None,
        0,
        ctypes.byref(output_blob),
    ):
        raise ctypes.WinError()
    try:
        return ctypes.string_at(output_blob.pbData, output_blob.cbData).decode("utf-8")
    finally:
        kernel32.LocalFree(output_blob.pbData)


def load_credentials():
    if CREDENTIALS_FILE.exists():
        try:
            with open(CREDENTIALS_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)
            if not isinstance(data, dict):
                return None
            username = str(data.get("username", ""))
            if not username:
                return None
            if data.get("password_dpapi"):
                try:
                    password = _unprotect_password(data["password_dpapi"])
                except Exception as e:
                    log.warning("Stored credentials could not be decrypted: %s", e)
                    return None
                return {"username": username, "password": password}

            # Accept the old format once so existing users can log in and be
            # migrated to DPAPI after a successful auto-login.
            password = data.get("password")
            if isinstance(password, str) and password:
                return {"username": username, "password": password, "legacy": True}
        except (OSError, json.JSONDecodeError) as e:
            log.warning("Ignoring unreadable credentials file %s: %s", CREDENTIALS_FILE, e)
    return None


def save_credentials(username, password):
    CREDENTIALS_FILE.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "version": 2,
        "username": username,
        "password_dpapi": _protect_password(password),
    }
    temp_path = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            dir=CREDENTIALS_FILE.parent,
            prefix="credentials-",
            suffix=".tmp",
            delete=False,
        ) as f:
            temp_path = Path(f.name)
            json.dump(payload, f, ensure_ascii=False, indent=2)
            f.flush()
            os.fsync(f.fileno())
        os.replace(temp_path, CREDENTIALS_FILE)
    finally:
        if temp_path is not None and temp_path.exists():
            try:
                temp_path.unlink()
            except OSError:
                pass


def clear_credentials():
    if CREDENTIALS_FILE.exists():
        try:
            CREDENTIALS_FILE.unlink()
        except OSError as e:
            log.warning("Could not remove credentials file %s: %s", CREDENTIALS_FILE, e)


# ─── jmcomic client management ──────────────────────────────────

def get_client():
    global _client
    if _client is None:
        with _client_lock:
            if _client is None:
                settings = load_settings()
                impl = settings.get("client_impl", "api")
                option = JmOption.default()
                _client = option.new_jm_client(impl=impl)
    return _client


def reset_client():
    global _client
    with _client_lock:
        _client = None


def get_option_for_download(settings=None):
    if settings is None:
        settings = load_settings()
    base_dir = settings.get("download_dir", str(DOWNLOAD_DIR))
    img_suffix = settings.get("image_format", ".jpg")
    os.makedirs(base_dir, exist_ok=True)
    cfg = {
        "dir_rule": {"rule": "Bd_Pname", "base_dir": base_dir},
        "download": {
            "cache": True,
            "image": {"decode": True, "suffix": img_suffix},
            "threading": {"image": 30, "photo": 4},
        },
        "client": {
            "impl": settings.get("client_impl", "api"),
            "retry_times": 5,
        },
    }
    return JmOption.construct(cfg)


# ─── Serialization helpers ───────────────────────────────────────

def serialize_album_detail(album):
    if album is None:
        return None
    chapters = []
    for photo in album:
        chapters.append({
            "id": str(photo.photo_id),
            "title": getattr(photo, "title", ""),
            "sort": getattr(photo, "sort", 0),
        })
    tags = list(getattr(album, "tags", []) or [])
    return {
        "id": str(album.album_id),
        "title": getattr(album, "title", ""),
        "author": getattr(album, "author", ""),
        "tags": tags,
        "page_count": getattr(album, "page_count", None) or len(album) or getattr(album, "photo_count", 0),
        "likes": str(getattr(album, "likes", "") or getattr(album, "like_count", "")),
        "comment_count": getattr(album, "comment_count", 0) or getattr(album, "comments", 0),
        "works": list(getattr(album, "works", []) or []),
        "actors": list(getattr(album, "actors", []) or []),
        "pub_date": str(getattr(album, "pub_date", "") or getattr(album, "date", "")),
        "update_date": str(getattr(album, "update_date", "") or getattr(album, "update_at", "")),
        "description": getattr(album, "description", "") or getattr(album, "intro", ""),
        "chapters": chapters,
    }


def serialize_photo_detail(photo):
    if photo is None:
        return None
    images = []
    for img in photo:
        images.append({
            "index": getattr(img, "index", 0),
            "img_url": getattr(img, "img_url", ""),
            "is_scrambled": getattr(img, "is_scrambled", False),
        })
    return {
        "id": str(photo.photo_id),
        "title": getattr(photo, "title", ""),
        "sort": getattr(photo, "sort", 0),
        "images": images,
    }


def serialize_search_item(album_id, title):
    return {"id": str(album_id), "title": title}


def search_item_full(page, album_id, title):
    """Try to get more info from a search page item."""
    item = {"id": str(album_id), "title": title}
    try:
        for a in page.iter_new_album():
            if str(getattr(a, "album_id", "")) == str(album_id):
                item["author"] = getattr(a, "author", "")
                item["tags"] = list(getattr(a, "tags", []) or [])
                item["likes"] = getattr(a, "likes", "")
                item["page_count"] = getattr(a, "page_count", 0)
                item["category"] = getattr(a, "category", "")
                break
    except Exception:
        pass
    return item


# ─── Error wrapper ───────────────────────────────────────────────

def api_error_handler(f):
    @wraps(f)
    def wrapper(*args, **kwargs):
        try:
            return f(*args, **kwargs)
        except HTTPException as e:
            return jsonify({"error": e.description}), e.code
        except Exception as e:
            log.error(f"API error in {f.__name__}: {e}")
            log.error(traceback.format_exc())
            return jsonify({"error": str(e)}), 500
    return wrapper


# ══════════════════════════════════════════════════════════════════
#  API ENDPOINTS
# ══════════════════════════════════════════════════════════════════

# ─── Health ───────────────────────────────────────────────────────

@app.route("/api/health", methods=["GET"])
def health():
    return jsonify({"status": "ok", "service": "jmcomic-backend", "auth_required": True})


# ─── Auth ─────────────────────────────────────────────────────────

@app.route("/api/auth/login", methods=["POST"])
@api_error_handler
def login():
    data = _json_object()
    username = data.get("username", "")
    password = data.get("password", "")
    remember = data.get("remember", True)
    if not username or not password:
        return jsonify({"error": "请输入用户名和密码"}), 400
    client = get_client()
    client.login(username, password)
    if remember:
        try:
            save_credentials(username, password)
        except Exception as e:
            # Login must not be reported as failed merely because optional
            # credential persistence is unavailable.
            log.error("Could not persist credentials securely: %s", e)
    else:
        clear_credentials()
    return jsonify({"success": True, "username": username})


@app.route("/api/auth/auto-login", methods=["POST"])
@api_error_handler
def auto_login():
    creds = load_credentials()
    if not creds:
        return jsonify({"success": False, "message": "无保存的凭据"})
    client = get_client()
    try:
        client.login(creds["username"], creds["password"])
        if creds.get("legacy"):
            try:
                save_credentials(creds["username"], creds["password"])
            except Exception as e:
                log.error("Could not migrate legacy credentials securely: %s", e)
        return jsonify({"success": True, "username": creds["username"]})
    except Exception as e:
        clear_credentials()
        return jsonify({"success": False, "message": str(e)})


@app.route("/api/auth/logout", methods=["POST"])
def logout():
    clear_credentials()
    reset_client()
    return jsonify({"success": True})


# ─── Search ──────────────────────────────────────────────────────

@app.route("/api/search", methods=["GET"])
@api_error_handler
def search():
    query = request.args.get("query", "").strip()
    page_num = _positive_int_arg("page", 1)
    category = request.args.get("category", "").strip()
    order_by = request.args.get("order_by", JmMagicConstants.ORDER_BY_LATEST)
    if not query:
        return jsonify({"error": "请输入搜索关键词"}), 400
    client = get_client()
    kwargs = {"search_query": query, "page": page_num, "order_by": order_by}
    if category:
        kwargs["category"] = category
    try:
        page = client.search_site(**kwargs)
    except Exception as e:
        log.warning(f"Search with category failed: {e}")
        page = client.search_site(search_query=query, page=page_num, order_by=order_by)
    results = []
    for aid, atitle in page.iter_id_title():
        results.append({"id": str(aid), "title": atitle})
    return jsonify({
        "results": results,
        "total": getattr(page, "total", len(results)),
        "page": page_num,
        "page_size": getattr(page, "page_size", 80),
        "page_count": getattr(page, "page_count", 1),
    })


# ─── Album detail ────────────────────────────────────────────────

@app.route("/api/album/<album_id>", methods=["GET"])
@api_error_handler
def album_detail(album_id):
    album_id = _validate_identifier(album_id, "album_id")
    client = get_client()
    album = client.get_album_detail(album_id)
    return jsonify(serialize_album_detail(album))


# ─── Photo (chapter) detail ──────────────────────────────────────

@app.route("/api/photo/<photo_id>", methods=["GET"])
@api_error_handler
def photo_detail(photo_id):
    photo_id = _validate_identifier(photo_id, "photo_id")
    client = get_client()
    photo = client.get_photo_detail(photo_id)
    return jsonify(serialize_photo_detail(photo))


# ─── Image proxy (serve decoded manga page images) ───────────────

# Cache: photo_id -> directory path with decoded images
_photo_cache = {}
_photo_cache_lock = threading.Lock()
_download_events = {}  # photo_id -> threading.Event (signals download complete)
_download_errors = {}  # photo_id -> error string
_temp_root = APP_DATA_DIR / "temp_images"


@app.route("/api/download_chapter/<photo_id>", methods=["POST"])
@api_error_handler
def download_chapter(photo_id):
    """Trigger background chapter download. Returns immediately."""
    photo_id = _validate_identifier(photo_id, "photo_id")
    with _photo_cache_lock:
        if photo_id in _photo_cache and _chapter_has_images(_photo_cache[photo_id]):
            return jsonify({"status": "ready"})
        _photo_cache.pop(photo_id, None)
        if photo_id in _download_events and not _download_events[photo_id].is_set():
            return jsonify({"status": "downloading"})
        _download_events.pop(photo_id, None)
        _download_errors.pop(photo_id, None)
        # Create event BEFORE releasing lock to prevent race condition
        event = threading.Event()
        _download_events[photo_id] = event

    def do_download():
        try:
            tmp_dir = str((_temp_root / photo_id).resolve())
            os.makedirs(tmp_dir, exist_ok=True)
            settings = load_settings()
            option = JmOption.construct({
                "dir_rule": {"rule": "Bd_Pid", "base_dir": tmp_dir},
                "download": {
                    "cache": True,
                    "image": {"decode": True, "suffix": None},
                    "threading": {"image": 10, "photo": 1},
                },
                "client": {"impl": settings.get("client_impl", "api"), "retry_times": 3},
            })
            photo, downloader = download_photo(photo_id, option)

            # Keep the chapter root in the cache so images in nested folders
            # remain visible and verify that decoding produced real files.
            if not _chapter_has_images(tmp_dir):
                raise RuntimeError("章节下载完成但没有找到已解码的图片")

            with _photo_cache_lock:
                _photo_cache[photo_id] = tmp_dir
            log.info(f"Chapter {photo_id} downloaded to {tmp_dir}")
        except Exception as e:
            log.error(f"Chapter download failed for {photo_id}: {e}")
            log.error(traceback.format_exc())
            with _photo_cache_lock:
                _photo_cache.pop(photo_id, None)
                _download_errors[photo_id] = str(e)
        finally:
            event.set()
            with _photo_cache_lock:
                _download_events.pop(photo_id, None)

    threading.Thread(target=do_download, daemon=True).start()
    return jsonify({"status": "downloading"})


@app.route("/api/chapter_status/<photo_id>", methods=["GET"])
@api_error_handler
def chapter_status(photo_id):
    photo_id = _validate_identifier(photo_id, "photo_id")
    with _photo_cache_lock:
        if photo_id in _photo_cache and _chapter_has_images(_photo_cache[photo_id]):
            return jsonify({"status": "ready"})
        _photo_cache.pop(photo_id, None)
        if photo_id in _download_errors:
            return jsonify({"status": "error", "error": _download_errors[photo_id]})
        if photo_id in _download_events:
            return jsonify({"status": "downloading"})
    return jsonify({"status": "not_started"})


@app.route("/api/image", methods=["GET"])
@api_error_handler
def serve_image():
    photo_id = _validate_identifier(request.args.get("photo_id", ""), "photo_id")
    img_index = _positive_int_arg("index", 0)
    if img_index <= 0:
        return jsonify({"error": "index 必须是正整数"}), 400

    # Get chapter directory from cache
    chapter_dir = None
    with _photo_cache_lock:
        chapter_dir = _photo_cache.get(photo_id)

    if not chapter_dir or not _chapter_has_images(chapter_dir):
        return jsonify({"error": "Chapter not downloaded yet"}), 404

    # Find image file by exact page stem (00001.jpg, 00001.png, etc.).
    target_stem = f"{img_index:05d}"
    all_files = _image_files(chapter_dir)
    for image_path in all_files:
        if image_path.stem == target_stem:
            return send_file(image_path)

    # Debug: log what files exist
    log.warning(f"Image {img_index} (stem {target_stem}) not found in {chapter_dir}. "
                f"Files found: {[p.name for p in all_files[:10]]}")
    return jsonify({"error": f"Image {img_index} not found"}), 404


# ─── Cover image ─────────────────────────────────────────────────

@app.route("/api/cover/<album_id>", methods=["GET"])
@api_error_handler
def serve_cover(album_id):
    album_id = _validate_identifier(album_id, "album_id")

    import requests as req_lib

    # Try direct CDN URL first (faster and more reliable)
    try:
        headers = {
            'Accept': 'image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8',
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
            'Referer': 'https://18comic.vip/',
        }
        for domain in JmModuleConfig.DOMAIN_IMAGE_LIST:
            try:
                url = f"https://{domain}/media/albums/{album_id}.jpg"
                resp = req_lib.get(url, headers=headers, timeout=8)
                if resp.status_code == 200 and len(resp.content) > 100:
                    return Response(resp.content, mimetype="image/jpeg")
            except Exception:
                continue
    except Exception:
        pass

    # Fallback: try via jmcomic client
    temp_path = None
    try:
        tmp = tempfile.NamedTemporaryFile(suffix=".jpg", delete=False)
        tmp_path = tmp.name
        tmp.close()
        client = get_client()
        client.download_album_cover(album_id, tmp_path)
        if os.path.exists(tmp_path) and os.path.getsize(tmp_path) > 0:
            response_path = tmp_path

            @after_this_request
            def remove_cover_temp(response):
                try:
                    os.unlink(response_path)
                except OSError:
                    pass
                return response

            response = send_file(response_path, mimetype="image/jpeg")
            temp_path = None
            return response
    except Exception as e:
        log.warning(f"Cover via client also failed for {album_id}: {e}")
    finally:
        if temp_path is not None and os.path.exists(temp_path):
            try:
                os.unlink(temp_path)
            except OSError:
                pass

    return jsonify({"error": "Cover not available"}), 404


# ─── Favorites ───────────────────────────────────────────────────

@app.route("/api/favorites", methods=["GET"])
@api_error_handler
def get_favorites():
    page_num = _positive_int_arg("page", 1)
    folder_id = request.args.get("folder_id", "0")
    order_by = request.args.get("order_by", JmMagicConstants.ORDER_BY_LATEST)
    client = get_client()
    page = client.favorite_folder(
        page=page_num,
        order_by=order_by,
        folder_id=folder_id,
    )
    results = []
    for aid, atitle in page.iter_id_title():
        results.append({"id": str(aid), "title": atitle})
    folders = []
    try:
        for fid, fname in page.iter_folder_id_name():
            folders.append({"id": str(fid), "name": fname})
    except Exception:
        pass
    return jsonify({
        "results": results,
        "page": page_num,
        "page_size": getattr(page, "page_size", 20),
        "folders": folders,
    })


@app.route("/api/favorites/add", methods=["POST"])
@api_error_handler
def add_favorite():
    data = _json_object()
    album_id = _validate_identifier(data.get("album_id", ""), "album_id")
    client = get_client()
    client.add_favorite_to_folder(album_id)
    return jsonify({"success": True})


# ─── Download ────────────────────────────────────────────────────

@app.route("/api/download", methods=["POST"])
@api_error_handler
def start_download():
    global _job_counter
    data = _json_object()
    album_id = _validate_identifier(data.get("album_id", ""), "album_id")
    settings = load_settings()
    output_fmt = str(data.get("output_format") or settings.get("output_format", "image")).lower()
    img_fmt = str(data.get("image_format") or settings.get("image_format", ".jpg")).lower()
    if output_fmt not in _ALLOWED_OUTPUT_FORMATS:
        raise BadRequest("不支持的输出格式")
    if img_fmt not in _ALLOWED_IMAGE_FORMATS:
        raise BadRequest("不支持的图片格式")
    dest_dir = data.get("download_dir") or settings.get("download_dir") or str(DOWNLOAD_DIR)
    if not isinstance(dest_dir, str) or not dest_dir.strip():
        raise BadRequest("download_dir 必须是非空字符串")
    dest_dir = _normalize_download_dir(dest_dir)
    os.makedirs(dest_dir, exist_ok=True)
    log.info(f"Download request: album={album_id}, output={output_fmt}, img_fmt={img_fmt}, dir={dest_dir}")
    with _job_lock:
        _job_counter += 1
        job_id = str(_job_counter)
        cancel_event = threading.Event()
        _download_cancel_events[job_id] = cancel_event
    _download_jobs[job_id] = {
        "status": "running",
        "album_id": album_id,
        "title": "",
        "progress": 0,
        "total": 0,
        "error": None,
        "path": None,
        "start_time": time.time(),
    }

    def do_download():
        try:
            if cancel_event.is_set():
                raise DownloadCancelled()
            cfg = {
                "dir_rule": {"rule": "Bd_Pname", "base_dir": dest_dir},
                "download": {
                    "cache": False,
                    "image": {"decode": True, "suffix": img_fmt},
                    "threading": {"image": 30, "photo": 4},
                },
                "client": {"impl": settings.get("client_impl", "api"), "retry_times": 5},
            }
            option = JmOption.construct(cfg)
            if cancel_event.is_set():
                raise DownloadCancelled()
            _download_jobs[job_id]["status"] = "downloading"

            album, downloader = download_album(album_id, option, check_exception=True)
            if cancel_event.is_set():
                raise DownloadCancelled()
            album_title = getattr(album, "title", str(album_id)) or str(album_id)
            _download_jobs[job_id]["title"] = album_title
            log.info(f"Album {album_id} downloaded: {album_title}")
            album_root = _album_root_from_option(option, album, dest_dir)

            if output_fmt == "pdf":
                from PIL import Image as PILImage

                image_files = _image_files(album_root)
                log.info(f"Found {len(image_files)} images for PDF in {album_root}")
                if not image_files:
                    raise RuntimeError("PDF 转换失败：当前漫画目录中没有图片")

                safe_title = "".join(c for c in album_title if c not in '\\/:*?"<>|').strip()[:80]
                pdf_path = Path(dest_dir) / f"[JM{album_id}]{safe_title}.pdf"
                converted_images = []
                try:
                    for image_file in image_files:
                        if cancel_event.is_set():
                            raise DownloadCancelled()
                        with PILImage.open(image_file) as source:
                            converted_images.append(source.convert("RGB"))
                    converted_images[0].save(
                        pdf_path,
                        "PDF",
                        save_all=True,
                        append_images=converted_images[1:],
                        resolution=100,
                    )
                finally:
                    for image in converted_images:
                        image.close()
                if not pdf_path.exists() or pdf_path.stat().st_size == 0:
                    raise RuntimeError("PDF 转换失败：输出文件为空")
                _download_jobs[job_id]["path"] = str(pdf_path)
                log.info(f"PDF saved: {pdf_path}")
            else:
                _download_jobs[job_id]["path"] = str(album_root if album_root.exists() else Path(dest_dir))

            with _job_lock:
                if cancel_event.is_set():
                    raise DownloadCancelled()
                _download_jobs[job_id]["status"] = "completed"
                _download_jobs[job_id]["progress"] = 100
                _download_jobs[job_id]["total"] = 100
            log.info(f"Download job {job_id} completed for album {album_id}")
        except DownloadCancelled:
            _download_jobs[job_id]["status"] = "cancelled"
            _download_jobs[job_id]["error"] = None
            log.info(f"Download job {job_id} cancelled for album {album_id}")
        except Exception as e:
            _download_jobs[job_id]["status"] = "error"
            _download_jobs[job_id]["error"] = str(e)
            log.error(f"Download job {job_id} failed: {e}")
            log.error(traceback.format_exc())
        finally:
            with _job_lock:
                _download_cancel_events.pop(job_id, None)

    t = threading.Thread(target=do_download, daemon=True)
    t.start()
    return jsonify({"job_id": job_id})


@app.route("/api/download/status/<job_id>", methods=["GET"])
def download_status(job_id):
    job = _download_jobs.get(str(job_id))
    if not job:
        return jsonify({"error": "Job not found"}), 404
    return jsonify(job)


@app.route("/api/download/cancel/<job_id>", methods=["POST"])
def cancel_download(job_id):
    job_id = str(job_id)
    with _job_lock:
        if job_id in _download_jobs:
            job = _download_jobs[job_id]
            if job["status"] in {"completed", "error", "cancelled"}:
                return jsonify({"success": True, "status": job["status"]})
            cancel_event = _download_cancel_events.get(job_id)
            if cancel_event is not None:
                cancel_event.set()
            job["status"] = "cancelled"
            return jsonify({"success": True, "status": "cancelled"})
    return jsonify({"error": "Job not found"}), 404


# ─── Rankings ────────────────────────────────────────────────────

@app.route("/api/ranking", methods=["GET"])
@api_error_handler
def ranking():
    rank_type = request.args.get("type", "day")
    category = request.args.get("category", JmMagicConstants.CATEGORY_ALL)
    page_num = _positive_int_arg("page", 1)
    client = get_client()
    if rank_type == "day":
        time_val = JmMagicConstants.TIME_TODAY
    elif rank_type == "week":
        time_val = JmMagicConstants.TIME_WEEK
    elif rank_type == "month":
        time_val = JmMagicConstants.TIME_MONTH
    else:
        time_val = JmMagicConstants.TIME_ALL
    try:
        page = client.categories_filter(
            page=page_num,
            time=time_val,
            category=category,
            order_by=JmMagicConstants.ORDER_BY_VIEW,
        )
    except Exception as e:
        log.warning(f"Ranking with category failed: {e}")
        # Fallback: use ranking methods
        if rank_type == "month":
            page = client.month_ranking(page_num)
        elif rank_type == "week":
            page = client.week_ranking(page_num)
        else:
            page = client.categories_filter(
                page=page_num,
                time=time_val,
                category=category,
                order_by=JmMagicConstants.ORDER_BY_VIEW,
            )
    results = []
    for aid, atitle in page.iter_id_title():
        results.append({"id": str(aid), "title": atitle})
    return jsonify({
        "results": results,
        "page": page_num,
        "page_size": getattr(page, "page_size", 80),
        "page_count": getattr(page, "page_count", 1),
    })


# ─── Homepage / Recommendations ──────────────────────────────────

@app.route("/api/recommend", methods=["GET"])
@api_error_handler
def recommend():
    page_num = _positive_int_arg("page", 1)
    category = request.args.get("category", JmMagicConstants.CATEGORY_ALL)
    order_by = request.args.get("order_by", JmMagicConstants.ORDER_BY_LATEST)
    client = get_client()
    page = client.categories_filter(
        page=page_num,
        time=JmMagicConstants.TIME_ALL,
        category=category,
        order_by=order_by,
    )
    results = []
    for aid, atitle in page.iter_id_title():
        results.append({"id": str(aid), "title": atitle})
    return jsonify({
        "results": results,
        "page": page_num,
        "page_size": getattr(page, "page_size", 80),
        "page_count": getattr(page, "page_count", 1),
    })


# ─── Categories constant list ────────────────────────────────────

@app.route("/api/categories", methods=["GET"])
def categories():
    cats = [
        {"id": JmMagicConstants.CATEGORY_ALL, "name": "全部"},
        {"id": JmMagicConstants.CATEGORY_DOUJIN, "name": "同人"},
        {"id": JmMagicConstants.CATEGORY_SINGLE, "name": "单本"},
        {"id": JmMagicConstants.CATEGORY_SHORT, "name": "短篇"},
        {"id": JmMagicConstants.CATEGORY_ANOTHER, "name": "其他"},
        {"id": JmMagicConstants.CATEGORY_HANMAN, "name": "韩漫"},
        {"id": JmMagicConstants.CATEGORY_MEIMAN, "name": "美漫"},
        {"id": JmMagicConstants.CATEGORY_DOUJIN_COSPLAY, "name": "Cosplay"},
        {"id": JmMagicConstants.CATEGORY_3D, "name": "3D"},
    ]
    return jsonify({"categories": cats})


# ─── Settings ────────────────────────────────────────────────────

@app.route("/api/settings", methods=["GET"])
@api_error_handler
def get_settings():
    return jsonify(load_settings())


@app.route("/api/settings", methods=["POST"])
@api_error_handler
def update_settings():
    data = _json_object()
    _validate_settings_update(data)
    settings = load_settings()
    settings.update(data)
    save_settings(settings)
    # If client_impl changed, reset the client
    if "client_impl" in data:
        reset_client()
    return jsonify({"success": True, "settings": settings})


# ══════════════════════════════════════════════════════════════════
#  Main
# ══════════════════════════════════════════════════════════════════

if __name__ == "__main__":
    os.makedirs(DOWNLOAD_DIR, exist_ok=True)
    os.makedirs(_temp_root, exist_ok=True)
    jmcomic.disable_jm_log()
    log.info("JMComic Backend starting on http://127.0.0.1:5000")
    log.info(f"App data: {APP_DATA_DIR}")
    app.run(host="127.0.0.1", port=5000, debug=False, threaded=True)
