"""
JMComic Client - Python Backend Server
Flask REST API wrapping the jmcomic library
"""
import os
import sys
import json
import time
import shutil
import logging
import threading
import traceback
from pathlib import Path
from functools import wraps

from flask import Flask, request, jsonify, send_file, Response
from flask_cors import CORS

# ─── jmcomic imports ────────────────────────────────────────────
import jmcomic
from jmcomic import (
    JmOption, JmModuleConfig, JmMagicConstants,
    download_album, download_photo, create_option_by_file, Feature,
)

# ─── App setup ───────────────────────────────────────────────────
APP_NAME = "禁漫天堂"
# Use AppData\Roaming\禁漫天堂 for persistent storage
APP_DATA_DIR = Path(os.environ.get("APPDATA", str(Path.home()))) / APP_NAME
APP_DATA_DIR.mkdir(parents=True, exist_ok=True)

SETTINGS_FILE = APP_DATA_DIR / "settings.json"
CREDENTIALS_FILE = APP_DATA_DIR / "credentials.json"
DOWNLOAD_DIR = APP_DATA_DIR / "downloads"

app = Flask(__name__)
CORS(app)

logging.basicConfig(level=logging.INFO, format="[%(asctime)s] %(message)s")
log = logging.getLogger("jmcomic-backend")

# ─── Global state ────────────────────────────────────────────────
_client = None
_client_lock = threading.Lock()
_download_jobs = {}  # job_id -> {status, progress, total, error, path}
_job_counter = 0
_job_lock = threading.Lock()

# ─── Settings helpers ────────────────────────────────────────────

def load_settings():
    if SETTINGS_FILE.exists():
        with open(SETTINGS_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    return {
        "download_dir": str(DOWNLOAD_DIR),
        "image_format": ".jpg",
        "output_format": "image",
        "client_impl": "api",
    }


def save_settings(settings):
    with open(SETTINGS_FILE, "w", encoding="utf-8") as f:
        json.dump(settings, f, ensure_ascii=False, indent=2)


def load_credentials():
    if CREDENTIALS_FILE.exists():
        with open(CREDENTIALS_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    return None


def save_credentials(username, password):
    with open(CREDENTIALS_FILE, "w", encoding="utf-8") as f:
        json.dump({"username": username, "password": password}, f)


def clear_credentials():
    if CREDENTIALS_FILE.exists():
        CREDENTIALS_FILE.unlink()


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
    return jsonify({"status": "ok"})


# ─── Auth ─────────────────────────────────────────────────────────

@app.route("/api/auth/login", methods=["POST"])
@api_error_handler
def login():
    data = request.json
    username = data.get("username", "")
    password = data.get("password", "")
    remember = data.get("remember", True)
    if not username or not password:
        return jsonify({"error": "请输入用户名和密码"}), 400
    client = get_client()
    client.login(username, password)
    if remember:
        save_credentials(username, password)
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
    page_num = int(request.args.get("page", 1))
    category = request.args.get("category", "").strip()
    order_by = request.args.get("order_by", JmMagicConstants.ORDER_BY_LATEST)
    if not query:
        return jsonify({"error": "请输入搜索关键词"}), 400
    client = get_client()
    kwargs = {"search_query": query, "page": page_num}
    if category:
        kwargs["category"] = category
    try:
        page = client.search_site(**kwargs)
    except Exception as e:
        log.warning(f"Search with category failed: {e}")
        page = client.search_site(search_query=query, page=page_num)
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
    client = get_client()
    album = client.get_album_detail(album_id)
    return jsonify(serialize_album_detail(album))


# ─── Photo (chapter) detail ──────────────────────────────────────

@app.route("/api/photo/<photo_id>", methods=["GET"])
@api_error_handler
def photo_detail(photo_id):
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
    with _photo_cache_lock:
        if photo_id in _photo_cache and os.path.exists(_photo_cache[photo_id]):
            return jsonify({"status": "ready"})
        if photo_id in _download_events:
            return jsonify({"status": "downloading"})
        # Create event BEFORE releasing lock to prevent race condition
        event = threading.Event()
        _download_events[photo_id] = event

    def do_download():
        try:
            tmp_dir = str(_temp_root / str(photo_id))
            os.makedirs(tmp_dir, exist_ok=True)
            option = JmOption.construct({
                "dir_rule": {"rule": "Bd_Pid", "base_dir": tmp_dir},
                "download": {
                    "cache": True,
                    "image": {"decode": True, "suffix": None},
                    "threading": {"image": 10, "photo": 1},
                },
                "client": {"impl": "api", "retry_times": 3},
            })
            photo, downloader = download_photo(photo_id, option)

            # Find where files were actually saved
            actual_dir = tmp_dir
            for root, dirs, files in os.walk(tmp_dir):
                image_files = [f for f in files if f.endswith(('.jpg', '.png', '.webp', '.gif'))]
                if image_files:
                    actual_dir = root
                    break

            with _photo_cache_lock:
                _photo_cache[photo_id] = actual_dir
            log.info(f"Chapter {photo_id} downloaded to {actual_dir}")
        except Exception as e:
            log.error(f"Chapter download failed for {photo_id}: {e}")
            import traceback
            log.error(traceback.format_exc())
            with _photo_cache_lock:
                _download_errors[photo_id] = str(e)
        finally:
            event.set()

    threading.Thread(target=do_download, daemon=True).start()
    return jsonify({"status": "downloading"})


@app.route("/api/chapter_status/<photo_id>", methods=["GET"])
def chapter_status(photo_id):
    with _photo_cache_lock:
        if photo_id in _photo_cache and os.path.exists(_photo_cache[photo_id]):
            return jsonify({"status": "ready"})
        if photo_id in _download_errors:
            return jsonify({"status": "error", "error": _download_errors[photo_id]})
        if photo_id in _download_events:
            return jsonify({"status": "downloading"})
    return jsonify({"status": "not_started"})


@app.route("/api/image", methods=["GET"])
@api_error_handler
def serve_image():
    url = request.args.get("url", "")
    photo_id = request.args.get("photo_id", "")
    img_index = int(request.args.get("index", 0))

    if not photo_id or img_index <= 0:
        return jsonify({"error": "Need photo_id and index"}), 400

    # Get chapter directory from cache
    chapter_dir = None
    with _photo_cache_lock:
        chapter_dir = _photo_cache.get(photo_id)

    if not chapter_dir or not os.path.exists(chapter_dir):
        return jsonify({"error": "Chapter not downloaded yet"}), 404

    # Find image file by index (00001.jpg, 00001.png, etc.)
    target_prefix = f"{img_index:05d}"
    all_files = []
    for root, dirs, files in os.walk(chapter_dir):
        for fname in files:
            fpath = os.path.join(root, fname)
            all_files.append(fpath)
            if fname.startswith(target_prefix) and os.path.getsize(fpath) > 0:
                return send_file(fpath)

    # Debug: log what files exist
    log.warning(f"Image {img_index} (prefix {target_prefix}) not found in {chapter_dir}. "
                f"Files found: {[os.path.basename(f) for f in all_files[:10]]}")
    return jsonify({"error": f"Image {img_index} not found"}), 404

    return jsonify({"error": f"Image {img_index} not found in chapter {photo_id}"}), 404


# ─── Cover image ─────────────────────────────────────────────────

@app.route("/api/cover/<album_id>", methods=["GET"])
@api_error_handler
def serve_cover(album_id):
    if not album_id:
        return jsonify({"error": "Missing album_id"}), 400

    import tempfile
    import requests as req_lib
    import urllib3
    urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

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
                resp = req_lib.get(url, headers=headers, timeout=8, verify=False)
                if resp.status_code == 200 and len(resp.content) > 100:
                    return Response(resp.content, mimetype="image/jpeg")
            except Exception:
                continue
    except Exception:
        pass

    # Fallback: try via jmcomic client
    try:
        tmp = tempfile.NamedTemporaryFile(suffix=".jpg", delete=False)
        tmp_path = tmp.name
        tmp.close()
        client = get_client()
        client.download_album_cover(album_id, tmp_path)
        if os.path.exists(tmp_path) and os.path.getsize(tmp_path) > 0:
            return send_file(tmp_path, mimetype="image/jpeg")
    except Exception as e:
        log.warning(f"Cover via client also failed for {album_id}: {e}")

    return jsonify({"error": "Cover not available"}), 404


# ─── Favorites ───────────────────────────────────────────────────

@app.route("/api/favorites", methods=["GET"])
@api_error_handler
def get_favorites():
    page_num = int(request.args.get("page", 1))
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
        "page_size": 20,
        "folders": folders,
    })


@app.route("/api/favorites/add", methods=["POST"])
@api_error_handler
def add_favorite():
    data = request.json
    album_id = str(data.get("album_id", ""))
    if not album_id:
        return jsonify({"error": "缺少 album_id"}), 400
    client = get_client()
    client.add_favorite_to_folder(album_id)
    return jsonify({"success": True})


# ─── Download ────────────────────────────────────────────────────

@app.route("/api/download", methods=["POST"])
@api_error_handler
def start_download():
    global _job_counter
    data = request.json
    album_id = str(data.get("album_id", ""))
    if not album_id:
        return jsonify({"error": "缺少 album_id"}), 400
    settings = load_settings()
    output_fmt = settings.get("output_format", "image")
    img_fmt = data.get("image_format", settings.get("image_format", ".jpg"))
    dest_dir = data.get("download_dir", "") or settings.get("download_dir", "") or str(DOWNLOAD_DIR)
    os.makedirs(dest_dir, exist_ok=True)
    log.info(f"Download request: album={album_id}, output={output_fmt}, img_fmt={img_fmt}, dir={dest_dir}")
    with _job_lock:
        _job_counter += 1
        job_id = str(_job_counter)
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
            cfg = {
                "dir_rule": {"rule": "Bd_Pname", "base_dir": dest_dir},
                "download": {
                    "cache": False,
                    "image": {"decode": True, "suffix": img_fmt},
                    "threading": {"image": 30, "photo": 4},
                },
                "client": {"impl": "api", "retry_times": 5},
            }
            option = JmOption.construct(cfg)
            _download_jobs[job_id]["status"] = "downloading"

            album, downloader = download_album(album_id, option, check_exception=True)
            album_title = getattr(album, "title", str(album_id)) or str(album_id)
            _download_jobs[job_id]["title"] = album_title
            log.info(f"Album {album_id} downloaded: {album_title}")

            if output_fmt == "pdf":
                try:
                    from PIL import Image as PILImage
                    safe_title = "".join(c for c in album_title if c not in '\\/:*?"<>|')[:80]
                    pdf_path = os.path.join(dest_dir, f"[JM{album_id}]{safe_title}.pdf")
                    image_files = []
                    for root, _, files in os.walk(dest_dir):
                        for f in sorted(files):
                            fpath = os.path.join(root, f)
                            if fpath.lower().endswith(('.jpg', '.jpeg', '.png', '.webp', '.gif')):
                                image_files.append(fpath)
                    log.info(f"Found {len(image_files)} images for PDF in {dest_dir}")
                    if image_files:
                        imgs = []
                        first = None
                        for fp in image_files:
                            im = PILImage.open(fp)
                            if im.mode == 'RGBA': im = im.convert('RGB')
                            if first is None: first = im
                            else: imgs.append(im)
                        if first:
                            first.save(pdf_path, "PDF", save_all=True, append_images=imgs, resolution=100)
                            log.info(f"PDF saved: {pdf_path}")
                            _download_jobs[job_id]["path"] = pdf_path
                except Exception as pe:
                    log.error(f"PDF conversion failed: {pe}")
                    log.error(traceback.format_exc())

            _download_jobs[job_id]["status"] = "completed"
            _download_jobs[job_id]["progress"] = 100
            _download_jobs[job_id]["total"] = 100
            if not _download_jobs[job_id]["path"]:
                _download_jobs[job_id]["path"] = str(Path(dest_dir))
            log.info(f"Download job {job_id} completed for album {album_id}")
        except Exception as e:
            _download_jobs[job_id]["status"] = "error"
            _download_jobs[job_id]["error"] = str(e)
            log.error(f"Download job {job_id} failed: {e}")
            log.error(traceback.format_exc())
    t = threading.Thread(target=do_download, daemon=True)
    t.start()
    return jsonify({"job_id": job_id})


@app.route("/api/download/status/<job_id>", methods=["GET"])
def download_status(job_id):
    job = _download_jobs.get(job_id)
    if not job:
        return jsonify({"error": "Job not found"}), 404
    return jsonify(job)


@app.route("/api/download/cancel/<job_id>", methods=["POST"])
def cancel_download(job_id):
    if job_id in _download_jobs:
        _download_jobs[job_id]["status"] = "cancelled"
        return jsonify({"success": True})
    return jsonify({"error": "Job not found"}), 404


# ─── Rankings ────────────────────────────────────────────────────

@app.route("/api/ranking", methods=["GET"])
@api_error_handler
def ranking():
    rank_type = request.args.get("type", "day")
    category = request.args.get("category", JmMagicConstants.CATEGORY_ALL)
    page_num = int(request.args.get("page", 1))
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
    page_num = int(request.args.get("page", 1))
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
def get_settings():
    return jsonify(load_settings())


@app.route("/api/settings", methods=["POST"])
def update_settings():
    data = request.json
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
