import os
import shutil
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import Mock, patch


TEST_APPDATA = tempfile.mkdtemp(prefix="jmcomic-tests-")
os.environ["APPDATA"] = TEST_APPDATA
os.environ["JMCOMIC_API_TOKEN"] = "test-token"

import backend.server as server


class _Page:
    total = 1
    page_size = 80
    page_count = 1

    def iter_id_title(self):
        return [(123, "demo")]


class _Album:
    title = "test-album"


class _FakeOption:
    class _DirRule:
        def __init__(self, root):
            self.root = Path(root)

        def decide_album_root_dir(self, album):
            return str(self.root)

    def __init__(self, root):
        self.dir_rule = self._DirRule(root)


class ServerRegressionTests(unittest.TestCase):
    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(TEST_APPDATA, ignore_errors=True)

    def setUp(self):
        self.client = server.app.test_client()
        server._client = None
        server._download_jobs.clear()
        server._download_cancel_events.clear()
        server._photo_cache.clear()
        server._download_events.clear()
        server._download_errors.clear()

    def request_headers(self):
        return {"X-JMComic-Token": "test-token"}

    def get(self, path, **kwargs):
        headers = kwargs.pop("headers", {})
        headers.update(self.request_headers())
        return self.client.get(path, headers=headers, **kwargs)

    def post(self, path, **kwargs):
        headers = kwargs.pop("headers", {})
        headers.update(self.request_headers())
        return self.client.post(path, headers=headers, **kwargs)

    @staticmethod
    def wait_until(predicate, timeout=3):
        deadline = time.time() + timeout
        while time.time() < deadline:
            value = predicate()
            if value:
                return value
            time.sleep(0.02)
        return predicate()

    def test_api_requires_token_including_health(self):
        self.assertEqual(self.client.get("/api/health").status_code, 401)
        self.assertEqual(self.get("/api/health").status_code, 200)
        self.assertEqual(self.client.get("/api/categories").status_code, 401)
        self.assertEqual(self.get("/api/categories").status_code, 200)

    def test_invalid_numeric_arguments_return_400(self):
        self.assertEqual(self.get("/api/search?query=test&page=nope").status_code, 400)
        self.assertEqual(
            self.get("/api/image?photo_id=123&index=nope").status_code,
            400,
        )

    def test_search_forwards_order_by(self):
        fake_client = Mock()
        fake_client.search_site.return_value = _Page()
        with patch.object(server, "get_client", return_value=fake_client):
            response = self.get("/api/search?query=test&order_by=mv")
        self.assertEqual(response.status_code, 200)
        self.assertEqual(fake_client.search_site.call_args.kwargs["order_by"], "mv")

    def test_failed_chapter_can_be_retried(self):
        failure = Mock(side_effect=RuntimeError("synthetic failure"))
        with patch.object(server.JmOption, "construct", return_value=object()), \
                patch.object(server, "download_photo", failure):
            self.assertEqual(self.post("/api/download_chapter/123").status_code, 200)
            self.assertTrue(
                self.wait_until(lambda: failure.call_count == 1)
            )
            self.assertEqual(self.get("/api/chapter_status/123").get_json()["status"], "error")
            self.assertEqual(
                self.post("/api/download_chapter/123").get_json()["status"],
                "downloading",
            )
            self.assertTrue(self.wait_until(lambda: failure.call_count == 2))

    def test_cancelled_job_is_not_overwritten_by_worker(self):
        root = Path(TEST_APPDATA) / "cancel-download"
        started = threading.Event()
        release = threading.Event()

        def blocking_download(*args, **kwargs):
            started.set()
            release.wait(3)
            return _Album(), object()

        settings = {
            "download_dir": str(root),
            "image_format": ".jpg",
            "output_format": "image",
            "client_impl": "api",
        }
        with patch.object(server, "load_settings", return_value=settings), \
                patch.object(server.JmOption, "construct", return_value=_FakeOption(root)), \
                patch.object(server, "download_album", side_effect=blocking_download):
            job_id = self.post("/api/download", json={"album_id": "123"}).get_json()["job_id"]
            self.assertTrue(started.wait(2))
            self.assertEqual(self.post(f"/api/download/cancel/{job_id}").status_code, 200)
            release.set()
            final_status = self.wait_until(
                lambda: self.get(f"/api/download/status/{job_id}").get_json()["status"]
                if self.get(f"/api/download/status/{job_id}").get_json()["status"]
                in {"cancelled", "completed", "error"}
                else None
            )
        self.assertEqual(final_status, "cancelled")

    def test_pdf_without_images_is_an_error(self):
        root = Path(TEST_APPDATA) / "pdf-download"
        settings = {
            "download_dir": str(root),
            "image_format": ".jpg",
            "output_format": "pdf",
            "client_impl": "api",
        }
        with patch.object(server, "load_settings", return_value=settings), \
                patch.object(server.JmOption, "construct", return_value=_FakeOption(root)), \
                patch.object(server, "download_album", return_value=(_Album(), object())):
            job_id = self.post("/api/download", json={"album_id": "123"}).get_json()["job_id"]
            final_job = self.wait_until(
                lambda: self.get(f"/api/download/status/{job_id}").get_json()
                if self.get(f"/api/download/status/{job_id}").get_json()["status"]
                in {"completed", "error"}
                else None
            )
        self.assertEqual(final_job["status"], "error")
        self.assertFalse(list(root.glob("*.pdf")))

    def test_settings_reject_unknown_or_invalid_values(self):
        self.assertEqual(
            self.post("/api/settings", json={"output_format": "zip"}).status_code,
            400,
        )
        self.assertEqual(
            self.post("/api/settings", json={"unexpected": True}).status_code,
            400,
        )


if __name__ == "__main__":
    unittest.main()
