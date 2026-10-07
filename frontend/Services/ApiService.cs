using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using JMComicClient.Models;

namespace JMComicClient.Services
{
    public class ApiService
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;

        private static readonly JsonSerializerSettings _jsonSettings = new()
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy()
            },
            NullValueHandling = NullValueHandling.Ignore,
        };

        public ApiService(string baseUrl = "http://127.0.0.1:5000")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        private async Task<T> GetAsync<T>(string path)
        {
            var resp = await _http.GetAsync($"{_baseUrl}{path}");
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(json, _jsonSettings);
        }

        private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest data)
        {
            var json = JsonConvert.SerializeObject(data, _jsonSettings);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{_baseUrl}{path}", content);
            var respJson = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<TResponse>(respJson, _jsonSettings);
        }

        // ── Health ───────────────────────────────────────────────
        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"{_baseUrl}/api/health");
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ── Auth ─────────────────────────────────────────────────
        public async Task<LoginResponse> LoginAsync(string username, string password, bool remember = true)
        {
            return await PostAsync<object, LoginResponse>("/api/auth/login",
                new { username, password, remember });
        }

        public async Task<LoginResponse> AutoLoginAsync()
        {
            return await PostAsync<object, LoginResponse>("/api/auth/auto-login", new { });
        }

        public async Task LogoutAsync()
        {
            await _http.PostAsync($"{_baseUrl}/api/auth/logout", null);
        }

        // ── Search ───────────────────────────────────────────────
        public async Task<SearchResponse> SearchAsync(string query, int page = 1,
            string category = "", string orderBy = "mr")
        {
            var url = $"/api/search?query={Uri.EscapeDataString(query)}&page={page}&order_by={orderBy}";
            if (!string.IsNullOrEmpty(category))
                url += $"&category={Uri.EscapeDataString(category)}";
            return await GetAsync<SearchResponse>(url);
        }

        // ── Album detail ─────────────────────────────────────────
        public async Task<AlbumDetail> GetAlbumDetailAsync(string albumId)
        {
            return await GetAsync<AlbumDetail>($"/api/album/{albumId}");
        }

        // ── Photo detail ─────────────────────────────────────────
        public async Task<PhotoDetail> GetPhotoDetailAsync(string photoId)
        {
            return await GetAsync<PhotoDetail>($"/api/photo/{photoId}");
        }

        // ── Image URL helper ─────────────────────────────────────
        public string GetImageUrl(string imgUrl, bool scrambled = false,
            string photoId = "", int index = 0)
        {
            var url = $"{_baseUrl}/api/image?url={Uri.EscapeDataString(imgUrl)}";
            if (!string.IsNullOrEmpty(photoId))
                url += $"&photo_id={photoId}";
            if (index > 0)
                url += $"&index={index}";
            if (scrambled)
                url += "&scrambled=true";
            return url;
        }

        public string GetCoverUrl(string albumId)
        {
            return $"{_baseUrl}/api/cover/{albumId}";
        }

        // ── Chapter download ─────────────────────────────────────
        public async Task<SimpleResponse> DownloadChapterAsync(string photoId)
        {
            return await PostAsync<object, SimpleResponse>($"/api/download_chapter/{photoId}", new { });
        }

        public async Task<ChapterStatusResponse> GetChapterStatusAsync(string photoId)
        {
            return await GetAsync<ChapterStatusResponse>($"/api/chapter_status/{photoId}");
        }

        // ── Favorites ────────────────────────────────────────────
        public async Task<FavoritesResponse> GetFavoritesAsync(int page = 1,
            string folderId = "0", string orderBy = "mr")
        {
            return await GetAsync<FavoritesResponse>(
                $"/api/favorites?page={page}&folder_id={folderId}&order_by={orderBy}");
        }

        public async Task<SimpleResponse> AddFavoriteAsync(string albumId)
        {
            return await PostAsync<object, SimpleResponse>("/api/favorites/add",
                new { album_id = albumId });
        }

        // ── Download ─────────────────────────────────────────────
        public async Task<DownloadResponse> StartDownloadAsync(string albumId,
            string outputFormat = "image", string imageFormat = ".jpg",
            string downloadDir = "")
        {
            return await PostAsync<object, DownloadResponse>("/api/download",
                new { album_id = albumId, output_format = outputFormat,
                      image_format = imageFormat, download_dir = downloadDir });
        }

        public async Task<DownloadStatus> GetDownloadStatusAsync(string jobId)
        {
            return await GetAsync<DownloadStatus>($"/api/download/status/{jobId}");
        }

        public async Task CancelDownloadAsync(string jobId)
        {
            await _http.PostAsync($"{_baseUrl}/api/download/cancel/{jobId}", null);
        }

        // ── Rankings ─────────────────────────────────────────────
        public async Task<RankingResponse> GetRankingAsync(string type = "day",
            string category = "0", int page = 1)
        {
            return await GetAsync<RankingResponse>(
                $"/api/ranking?type={type}&category={category}&page={page}");
        }

        // ── Recommendations ──────────────────────────────────────
        public async Task<RecommendResponse> GetRecommendAsync(int page = 1,
            string category = "0", string orderBy = "mr")
        {
            return await GetAsync<RecommendResponse>(
                $"/api/recommend?page={page}&category={category}&order_by={orderBy}");
        }

        // ── Categories ───────────────────────────────────────────
        public async Task<CategoriesResponse> GetCategoriesAsync()
        {
            return await GetAsync<CategoriesResponse>("/api/categories");
        }

        // ── Settings ─────────────────────────────────────────────
        public async Task<AppSettings> GetSettingsAsync()
        {
            return await GetAsync<AppSettings>("/api/settings");
        }

        public async Task<SimpleResponse> UpdateSettingsAsync(AppSettings settings)
        {
            return await PostAsync<AppSettings, SimpleResponse>("/api/settings", settings);
        }
    }

    public class CategoriesResponse
    {
        public System.Collections.Generic.List<CategoryItem> Categories { get; set; }
    }
}
