using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using JMComicClient.Models;

namespace JMComicClient.Services
{
    public sealed class ApiException : Exception
    {
        public int StatusCode { get; }

        public ApiException(string message, int statusCode)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }

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

        public ApiService(string baseUrl = "http://127.0.0.1:5000", string apiToken = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            if (!string.IsNullOrWhiteSpace(apiToken))
                _http.DefaultRequestHeaders.Add("X-JMComic-Token", apiToken);
        }

        private static string EscapeSegment(string value) =>
            Uri.EscapeDataString(value ?? string.Empty);

        private static string GetErrorMessage(string json, int statusCode)
        {
            try
            {
                var error = JsonConvert.DeserializeObject<ApiError>(json, _jsonSettings)?.Error;
                if (!string.IsNullOrWhiteSpace(error)) return error;
            }
            catch { }
            return $"后端请求失败（HTTP {statusCode}）";
        }

        private async Task<T> GetAsync<T>(string path)
        {
            var resp = await _http.GetAsync($"{_baseUrl}{path}");
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new ApiException(GetErrorMessage(json, (int)resp.StatusCode), (int)resp.StatusCode);
            if (string.IsNullOrWhiteSpace(json)) return default;
            return JsonConvert.DeserializeObject<T>(json, _jsonSettings);
        }

        private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest data)
        {
            var json = JsonConvert.SerializeObject(data, _jsonSettings);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{_baseUrl}{path}", content);
            var respJson = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new ApiException(GetErrorMessage(respJson, (int)resp.StatusCode), (int)resp.StatusCode);
            if (string.IsNullOrWhiteSpace(respJson)) return default;
            return JsonConvert.DeserializeObject<TResponse>(respJson, _jsonSettings);
        }

        private async Task<byte[]> GetBytesAsync(string path)
        {
            var resp = await _http.GetAsync($"{_baseUrl}{path}");
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (!resp.IsSuccessStatusCode)
            {
                var text = Encoding.UTF8.GetString(bytes);
                throw new ApiException(GetErrorMessage(text, (int)resp.StatusCode), (int)resp.StatusCode);
            }
            return bytes;
        }

        // ── Health ───────────────────────────────────────────────
        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"{_baseUrl}/api/health");
                if (!resp.IsSuccessStatusCode) return false;
                var json = await resp.Content.ReadAsStringAsync();
                var health = JsonConvert.DeserializeObject<HealthResponse>(json, _jsonSettings);
                return health?.Status == "ok" && health.Service == "jmcomic-backend";
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
            await PostAsync<object, SimpleResponse>("/api/auth/logout", new { });
        }

        // ── Search ───────────────────────────────────────────────
        public async Task<SearchResponse> SearchAsync(string query, int page = 1,
            string category = "", string orderBy = "mr")
        {
            var url = $"/api/search?query={Uri.EscapeDataString(query)}&page={page}&order_by={EscapeSegment(orderBy)}";
            if (!string.IsNullOrEmpty(category))
                url += $"&category={Uri.EscapeDataString(category)}";
            return await GetAsync<SearchResponse>(url);
        }

        // ── Album detail ─────────────────────────────────────────
        public async Task<AlbumDetail> GetAlbumDetailAsync(string albumId)
        {
            return await GetAsync<AlbumDetail>($"/api/album/{EscapeSegment(albumId)}");
        }

        // ── Photo detail ─────────────────────────────────────────
        public async Task<PhotoDetail> GetPhotoDetailAsync(string photoId)
        {
            return await GetAsync<PhotoDetail>($"/api/photo/{EscapeSegment(photoId)}");
        }

        public Task<byte[]> GetCoverBytesAsync(string albumId)
        {
            return GetBytesAsync($"/api/cover/{EscapeSegment(albumId)}");
        }

        public Task<byte[]> GetImageBytesAsync(string imgUrl, string photoId, int index)
        {
            var path = $"/api/image?url={Uri.EscapeDataString(imgUrl ?? string.Empty)}" +
                       $"&photo_id={EscapeSegment(photoId)}&index={index}";
            return GetBytesAsync(path);
        }

        // ── Chapter download ─────────────────────────────────────
        public async Task<SimpleResponse> DownloadChapterAsync(string photoId)
        {
            return await PostAsync<object, SimpleResponse>($"/api/download_chapter/{EscapeSegment(photoId)}", new { });
        }

        public async Task<ChapterStatusResponse> GetChapterStatusAsync(string photoId)
        {
            return await GetAsync<ChapterStatusResponse>($"/api/chapter_status/{EscapeSegment(photoId)}");
        }

        // ── Favorites ────────────────────────────────────────────
        public async Task<FavoritesResponse> GetFavoritesAsync(int page = 1,
            string folderId = "0", string orderBy = "mr")
        {
            return await GetAsync<FavoritesResponse>(
                $"/api/favorites?page={page}&folder_id={EscapeSegment(folderId)}&order_by={EscapeSegment(orderBy)}");
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
            return await GetAsync<DownloadStatus>($"/api/download/status/{EscapeSegment(jobId)}");
        }

        public async Task CancelDownloadAsync(string jobId)
        {
            await PostAsync<object, SimpleResponse>($"/api/download/cancel/{EscapeSegment(jobId)}", new { });
        }

        // ── Rankings ─────────────────────────────────────────────
        public async Task<RankingResponse> GetRankingAsync(string type = "day",
            string category = "0", int page = 1)
        {
            return await GetAsync<RankingResponse>(
                $"/api/ranking?type={EscapeSegment(type)}&category={EscapeSegment(category)}&page={page}");
        }

        // ── Recommendations ──────────────────────────────────────
        public async Task<RecommendResponse> GetRecommendAsync(int page = 1,
            string category = "0", string orderBy = "mr")
        {
            return await GetAsync<RecommendResponse>(
                $"/api/recommend?page={page}&category={EscapeSegment(category)}&order_by={EscapeSegment(orderBy)}");
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

    internal class HealthResponse
    {
        public string Status { get; set; }
        public string Service { get; set; }
    }
}
