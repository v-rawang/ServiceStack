﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using ServiceStack.Web;

namespace ServiceStack
{
    public class HttpCacheFeature : IPlugin
    {
        public TimeSpan DefaultMaxAge { get; set; }
        public TimeSpan DefaultExpiresIn { get; set; }

        public Func<string, string> CacheControlFilter { get; set; }

        public string CacheControlForOptimizedResults { get; set; }

        public HttpCacheFeature()
        {
            DefaultMaxAge = TimeSpan.FromHours(1);
            DefaultExpiresIn = TimeSpan.FromMinutes(10);
            CacheControlForOptimizedResults = "max-age=0";
        }

        public void Register(IAppHost appHost)
        {
            appHost.GlobalResponseFilters.Add(HandleCacheResponses);
        }

        public void HandleCacheResponses(IRequest req, IResponse res, object response)
        {
            var cacheInfo = req.GetItem(Keywords.CacheInfo) as CacheInfo;
            if (cacheInfo != null && cacheInfo.CacheKey != null)
            {
                if (CacheAndWriteResponse(cacheInfo, req, res, response))
                    return;
            }

            var httpResult = response as HttpResult;
            if (httpResult == null)
                return;

            cacheInfo = httpResult.ToCacheInfo();

            if ((req.Verb != HttpMethods.Get && req.Verb != HttpMethods.Head) ||
                (httpResult.StatusCode != HttpStatusCode.OK && httpResult.StatusCode != HttpStatusCode.NotModified))
                return;

            if (httpResult.LastModified != null)
                httpResult.Headers[HttpHeaders.LastModified] = httpResult.LastModified.Value.ToUniversalTime().ToString("r");

            if (httpResult.ETag != null)
                httpResult.Headers[HttpHeaders.ETag] = httpResult.ETag.Quoted();

            if (httpResult.Expires != null)
                httpResult.Headers[HttpHeaders.Expires] = httpResult.Expires.Value.ToUniversalTime().ToString("r");

            if (httpResult.Age != null)
                httpResult.Headers[HttpHeaders.Age] = httpResult.Age.Value.TotalSeconds.ToString(CultureInfo.InvariantCulture);

            var alreadySpecifiedCacheControl = httpResult.Headers.ContainsKey(HttpHeaders.CacheControl);
            if (!alreadySpecifiedCacheControl)
            {
                var cacheControl = BuildCacheControlHeader(cacheInfo);
                if (cacheControl != null)
                    httpResult.Headers[HttpHeaders.CacheControl] = cacheControl;
            }

            if (req.ETagMatch(httpResult.ETag) || req.NotModifiedSince(httpResult.LastModified))
            {
                res.EndNotModified();
            }
        }

        private bool CacheAndWriteResponse(CacheInfo cacheInfo, IRequest req, IResponse res, object response)
        {
            var httpResult = response as IHttpResult;
            var dto = httpResult != null ? httpResult.Response : response;
            if (dto == null || dto is IPartialWriter || dto is IStreamWriter)
                return false;

            var expiresIn = cacheInfo.ExpiresIn.GetValueOrDefault(DefaultExpiresIn);
            var cache = cacheInfo.LocalCache ? HostContext.LocalCache : HostContext.Cache;

            var responseBytes = dto as byte[];
            if (responseBytes == null)
            {
                var rawStr = dto as string;
                if (rawStr != null)
                    responseBytes = rawStr.ToUtf8Bytes();
                else
                {
                    var stream = dto as Stream;
                    if (stream != null)
                        responseBytes = stream.ReadFully();
                }
            }

            var encoding = req.GetCompressionType();
            var cacheKeyEncoded = encoding != null ? cacheInfo.CacheKey + "." + encoding : null;
            if (responseBytes != null || req.ResponseContentType.IsBinary())
            {
                if (responseBytes == null)
                    responseBytes = HostContext.ContentTypes.SerializeToBytes(req, dto);

                cache.Set(cacheInfo.CacheKey, responseBytes, expiresIn);

                if (encoding != null)
                {
                    res.AddHeader(HttpHeaders.ContentEncoding, encoding);
                    responseBytes = responseBytes.CompressBytes(encoding);
                    cache.Set(cacheKeyEncoded, responseBytes, expiresIn);
                }
            }
            else
            {
                var serializedDto = req.SerializeToString(dto);
                if (req.ResponseContentType.MatchesContentType(MimeTypes.Json))
                {
                    var jsonp = req.GetJsonpCallback();
                    if (jsonp != null)
                        serializedDto = jsonp + "(" + serializedDto + ")";
                }

                responseBytes = serializedDto.ToUtf8Bytes();
                cache.Set(cacheInfo.CacheKey, responseBytes, expiresIn);

                if (encoding != null)
                {
                    responseBytes = responseBytes.CompressBytes(encoding);
                    cache.Set(cacheKeyEncoded, responseBytes, expiresIn);
                    res.AddHeader(HttpHeaders.ContentEncoding, encoding);
                }
            }

            var doHttpCaching = cacheInfo.MaxAge != null || cacheInfo.CacheControl != CacheControl.None;
            if (doHttpCaching)
            {
                var cacheControl = BuildCacheControlHeader(cacheInfo);
                if (cacheControl != null)
                {
                    var lastModified = cacheInfo.LastModified.GetValueOrDefault(DateTime.UtcNow);
                    cache.Set("date:" + cacheInfo.CacheKey, lastModified, expiresIn);
                    res.AddHeaderLastModified(lastModified);
                    res.AddHeader(HttpHeaders.CacheControl, cacheControl);
                }
            }

            if (httpResult != null)
            {
                foreach (var header in httpResult.Headers)
                {
                    res.AddHeader(header.Key, header.Value);
                }
            }

            res.WriteBytesToResponse(responseBytes, req.ResponseContentType);
            return true;
        }

        private string BuildCacheControlHeader(CacheInfo cacheInfo)
        {
            var maxAge = cacheInfo.MaxAge;
            if (maxAge == null && (cacheInfo.LastModified != null || cacheInfo.ETag != null))
                maxAge = DefaultMaxAge;

            var cacheHeader = new List<string>();
            if (maxAge != null)
                cacheHeader.Add("max-age=" + maxAge.Value.TotalSeconds);

            if (cacheInfo.CacheControl != CacheControl.None)
            {
                var cache = cacheInfo.CacheControl;
                if (cache.HasFlag(CacheControl.Public))
                    cacheHeader.Add("public");
                else if (cache.HasFlag(CacheControl.Private))
                    cacheHeader.Add("private");

                if (cache.HasFlag(CacheControl.MustRevalidate))
                    cacheHeader.Add("must-revalidate");
                if (cache.HasFlag(CacheControl.NoCache))
                    cacheHeader.Add("no-cache");
                if (cache.HasFlag(CacheControl.NoStore))
                    cacheHeader.Add("no-store");
                if (cache.HasFlag(CacheControl.NoTransform))
                    cacheHeader.Add("no-transform");
            }

            if (cacheHeader.Count <= 0)
                return null;

            var cacheControl = cacheHeader.ToArray().Join(", ");
            return CacheControlFilter != null 
                ? CacheControlFilter(cacheControl) 
                : cacheControl;
        }
    }

    public static class HttpCacheExtensions
    {
        public static void EndNotModified(this IResponse res, string description=null)
        {
            res.StatusCode = 304;
            res.StatusDescription = description ?? HostContext.ResolveLocalizedString(LocalizedStrings.NotModified);
            res.EndRequest();
        }

        public static bool ETagMatch(this IRequest req, string eTag)
        {
            if (string.IsNullOrEmpty(eTag))
                return false;

            return eTag.StripWeakRef().Quoted() == req.Headers[HttpHeaders.IfNoneMatch].StripWeakRef().Quoted();
        }

        public static bool NotModifiedSince(this IRequest req, DateTime? lastModified)
        {
            if (lastModified != null)
            {
                var ifModifiedSince = req.Headers[HttpHeaders.IfModifiedSince];
                if (ifModifiedSince != null)
                {
                    DateTime modifiedSinceDate;
                    if (DateTime.TryParse(ifModifiedSince, out modifiedSinceDate))
                        return modifiedSinceDate <= lastModified.Value;
                }
            }

            return false;
        }

        public static bool HasValidCache(this IRequest req, string eTag)
        {
            return req.ETagMatch(eTag);
        }

        public static bool HasValidCache(this IRequest req, DateTime? lastModified)
        {
            return req.NotModifiedSince(lastModified);
        }

        public static bool HasValidCache(this IRequest req, string eTag, DateTime? lastModified)
        {
            return req.ETagMatch(eTag) || req.NotModifiedSince(lastModified);
        }

        public static bool ShouldAddLastModifiedToOptimizedResults(this HttpCacheFeature feature)
        {
            return feature != null && feature.CacheControlForOptimizedResults != null;
        }

        internal static string StripWeakRef(this string eTag)
        {
            return eTag != null && eTag.StartsWith("W/")
                ? eTag.Substring(2) 
                : eTag;
        }
    }
}