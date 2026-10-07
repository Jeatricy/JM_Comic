using System.Collections.Generic;

namespace JMComicClient.Models
{
    public class ApiError
    {
        public string Error { get; set; }
    }

    public class LoginResponse
    {
        public bool Success { get; set; }
        public string Username { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
    }

    public class SearchResult
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public List<string> Tags { get; set; }
        public string Likes { get; set; }
        public int PageCount { get; set; }
        public string Category { get; set; }
    }

    public class SearchResponse
    {
        public List<SearchResult> Results { get; set; }
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int PageCount { get; set; }
        public string Error { get; set; }
    }

    public class AlbumChapter
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public int Sort { get; set; }
    }

    public class AlbumDetail
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public List<string> Tags { get; set; }
        public int PageCount { get; set; }
        public string Likes { get; set; }
        public int CommentCount { get; set; }
        public List<string> Works { get; set; }
        public List<string> Actors { get; set; }
        public string PubDate { get; set; }
        public string UpdateDate { get; set; }
        public string Description { get; set; }
        public List<AlbumChapter> Chapters { get; set; }
        public string Error { get; set; }
    }

    public class PageImage
    {
        public int Index { get; set; }
        public string ImgUrl { get; set; }
        public bool IsScrambled { get; set; }
    }

    public class PhotoDetail
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public int Sort { get; set; }
        public List<PageImage> Images { get; set; }
        public string Error { get; set; }
    }

    public class FolderInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class FavoritesResponse
    {
        public List<SearchResult> Results { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public List<FolderInfo> Folders { get; set; }
        public string Error { get; set; }
    }

    public class DownloadResponse
    {
        public string JobId { get; set; }
        public string Error { get; set; }
    }

    public class DownloadStatus
    {
        public string Status { get; set; }
        public string AlbumId { get; set; }
        public string Title { get; set; }
        public int Progress { get; set; }
        public int Total { get; set; }
        public string Error { get; set; }
        public string Path { get; set; }
    }

    public class CategoryItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class AppSettings
    {
        public string DownloadDir { get; set; } = "downloads";
        public string ImageFormat { get; set; } = ".jpg";
        public string OutputFormat { get; set; } = "image";
        public string ClientImpl { get; set; } = "api";
    }

    public class RecommendResponse
    {
        public List<SearchResult> Results { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int PageCount { get; set; }
        public string Error { get; set; }
    }

    public class RankingResponse
    {
        public List<SearchResult> Results { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int PageCount { get; set; }
        public string Error { get; set; }
    }

    public class SimpleResponse
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string Status { get; set; }
    }

    public class ChapterStatusResponse
    {
        public string Status { get; set; }
        public string Error { get; set; }
    }
}
