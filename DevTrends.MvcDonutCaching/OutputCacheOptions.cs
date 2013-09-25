﻿using System;

namespace DevTrends.MvcDonutCaching
{
    [Flags]
    public enum OutputCacheOptions
    {
        None = 0x0,
        /// <summary>
        /// No matter what, never use the query string parameters to generate the cache key name
        /// </summary>
        IgnoreQueryString = 0x1,
        /// <summary>
        /// No matter what, never use the Post data to generate the cache key name
        /// </summary>
        IgnoreFormData = 0x2,
        /// <summary>
        /// If the request is a POST, don't lookup for a cached result, execute the the result normally, 
        /// caching it for subsequent GET (or any other non POST verb).
        /// </summary>
        NoCacheLookupForPosts = 0x4,
    }
}
