﻿using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Helpers;
using System.Web.Mvc;
using System.Web.UI;

namespace DevTrends.MvcDonutCaching
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public class DonutOutputCacheAttribute : ActionFilterAttribute, IExceptionFilter
    {
        // Protected
        protected readonly ICacheHeadersHelper CacheHeadersHelper;
        protected readonly ICacheSettingsManager CacheSettingsManager;
        protected readonly IDonutHoleFiller DonutHoleFiller;
        protected readonly IKeyGenerator KeyGenerator;
        protected readonly IReadWriteOutputCacheManager OutputCacheManager;
        protected CacheSettings CacheSettings;

        // Private
        private bool? _noStore;
        private OutputCacheOptions? _options;

        public DonutOutputCacheAttribute() : this(new KeyBuilder()) { }

        public DonutOutputCacheAttribute(IKeyBuilder keyBuilder) :
            this(
               new KeyGenerator(keyBuilder),
               new OutputCacheManager(OutputCache.Instance, keyBuilder),
               new DonutHoleFiller(new EncryptingActionSettingsSerialiser(new ActionSettingsSerialiser(), new Encryptor())),
               new CacheSettingsManager(),
               new CacheHeadersHelper()
        )
        { }

        protected DonutOutputCacheAttribute(
            IKeyGenerator keyGenerator, IReadWriteOutputCacheManager outputCacheManager,
            IDonutHoleFiller donutHoleFiller, ICacheSettingsManager cacheSettingsManager, ICacheHeadersHelper cacheHeadersHelper
        )
        {
            KeyGenerator = keyGenerator;
            OutputCacheManager = outputCacheManager;
            DonutHoleFiller = donutHoleFiller;
            CacheSettingsManager = cacheSettingsManager;
            CacheHeadersHelper = cacheHeadersHelper;

            Duration = -1;
            Location = (OutputCacheLocation)(-1);
            Options = OutputCache.DefaultOptions;
        }

        /// <summary>
        /// Gets or sets the cache duration, in seconds.
        /// </summary>
        public int Duration
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the vary-by-param value.
        /// </summary>
        public string VaryByParam
        {
            get;
            set;
        }

        public string PrimaryKeyComponents { get; set; }

        public string IgnoreParamRegexes { get; set; }

        /// <summary>
        /// Gets or sets the vary-by-custom value.
        /// </summary>
        public string VaryByCustom
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the skip-by-custom value.
        /// </summary>
        public string SkipByCustom
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the cache profile name.
        /// </summary>
        public string CacheProfile
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the location.
        /// </summary>
        public OutputCacheLocation Location
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets a value that indicates whether to store the cache.
        /// </summary>
        public bool NoStore
        {
            get
            {
                return _noStore ?? false;
            }
            set
            {
                _noStore = value;
            }
        }

        /// <summary>
        /// Get or sets the <see cref="OutputCacheOptions"/> for this attributes. Specifying a value here will
        /// make the <see cref="OutputCache.DefaultOptions"/> value ignored.
        /// </summary>
        public OutputCacheOptions Options
        {
            get
            {
                return _options ?? OutputCacheOptions.None;
            }
            set
            {
                _options = value;
            }
        }

        public void OnException(ExceptionContext filterContext)
        {
            if (CacheSettings != null)
            {
                ExecuteCallback(filterContext, true);
            }
        }

        /// <summary>
        /// Called before an action method executes.
        /// </summary>
        /// <param name="filterContext">The filter context.</param>
        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            if (SkipBecauseOfCustom(filterContext))
            {
                return;
            }

            CacheSettings = BuildCacheSettings();

            var cacheKey = KeyGenerator.GenerateKey(filterContext, CacheSettings);

            // If we are unable to generate a cache key it means we can't do anything
            if (string.IsNullOrEmpty(cacheKey))
            {
                return;
            }

            // Are we actually storing data on the server side ?
            if (CacheSettings.IsServerCachingEnabled)
            {
                CacheItem cachedItem = null;

                // If the request is a POST, we lookup for NoCacheLookupForPosts option
                // We are fetching the stored value only if the option has not been set and the request is not a POST
                if (
                    (CacheSettings.Options & OutputCacheOptions.NoCacheLookupForPosts) != OutputCacheOptions.NoCacheLookupForPosts ||
                    filterContext.HttpContext.Request.HttpMethod != "POST"
                )
                {
                    cachedItem = OutputCacheManager.GetItem(cacheKey);
                }

                // We have a cached version on the server side
                if (cachedItem != null)
                {
                    Func<string, string> augmentActionResult = null;
                    //in the somewhat specialist case where:
                    //- we are returning a JSON object with some HTML in a property
                    //- that HTML was created with a donut hole in it
                    //- the action returning the JSON is in a DonutOutputCache action

                    //the replace text should be escaped for JSON but it isn't if it's evaluated here.  Hence we
                    //escape for JSON if the contenttype tells us it's json.
                    if (cachedItem.ContentType.Contains("json"))
                    {
                        augmentActionResult =
                            actionResult => HttpUtility.JavaScriptStringEncode(actionResult, addDoubleQuotes: false);
                    }

                    // We inject the previous result into the MVC pipeline
                    // The MVC action won't execute as we injected the previous cached result.
                    filterContext.Result = new ContentResult
                    {
                        Content = DonutHoleFiller.ReplaceDonutHoleContent(cachedItem.Content, filterContext, CacheSettings.Options, augmentActionResult),
                        ContentType = cachedItem.ContentType
                    };
                }
            }

            // Did we already injected something ?
            if (filterContext.Result != null)
            {
                return; // No need to continue 
            }

            // We are hooking into the pipeline to replace the response Output writer
            // by something we own and later eventually gonna cache
            var cachingWriter = new StringWriter(CultureInfo.InvariantCulture);

            var originalWriter = filterContext.HttpContext.Response.Output;

            filterContext.HttpContext.Response.Output = cachingWriter;

            // Will be called back by OnResultExecuted -> ExecuteCallback
            filterContext.HttpContext.Items[cacheKey] = new Action<bool>(hasErrors =>
            {
                // Removing this executing action from the context
                filterContext.HttpContext.Items.Remove(cacheKey);

                // We restore the original writer for response
                filterContext.HttpContext.Response.Output = originalWriter;

                if (hasErrors)
                {
                    return; // Something went wrong, we are not going to cache something bad
                }

                // Now we use owned caching writer to actually store data
                var cacheItem = new CacheItem
                {
                    Content = cachingWriter.ToString(),
                    ContentType = filterContext.HttpContext.Response.ContentType
                };

                filterContext.HttpContext.Response.Write(
                    DonutHoleFiller.RemoveDonutHoleWrappers(cacheItem.Content, filterContext, CacheSettings.Options)
                );

                if (SkipBecauseOfCustom(filterContext))
                {
                    return; //offer second chance to skip by custom in case something has changed in the course of the action (e.g. setting a flag to choose to skip)
                }

                if (CacheSettings.IsServerCachingEnabled && filterContext.HttpContext.Response.StatusCode == 200)
                {
                    OutputCacheManager.AddItem(cacheKey, cacheItem, DateTime.UtcNow.AddSeconds(GetDuration(filterContext)));
                }
            });
        }

        private int GetDuration(ControllerContext filterContext)
        {
            if (DurationByCustom != null)
            {
                var getCustomDurationDelegate =
                    filterContext.HttpContext.Application[HttpApplicationExtensions.DurationByCustomKey] as
                        Func<HttpContextBase, string, int?>;

                return getCustomDurationDelegate?.Invoke(filterContext.HttpContext, DurationByCustom) ?? CacheSettings.Duration;
            }

            return CacheSettings.Duration;
        }

        public string DurationByCustom { get; set; }

        private bool SkipBecauseOfCustom(ControllerContext filterContext)
        {
            if (SkipByCustom != null)
            {
                var skipByCustomDelegate =
                    filterContext.HttpContext.Application[HttpApplicationExtensions.SkipByCustomApplicationStateKey] as
                        Func<HttpContextBase, string, bool>;
                if (skipByCustomDelegate != null && skipByCustomDelegate(filterContext.HttpContext, SkipByCustom))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Called after an action result executes.
        /// </summary>
        /// <param name="filterContext">The filter context.</param>
        public override void OnResultExecuted(ResultExecutedContext filterContext)
        {
            if (CacheSettings == null)
            {
                return;
            }

            // See OnActionExecuting
            ExecuteCallback(filterContext, filterContext.Exception != null);

            // If we are in the context of a child action, the main action is responsible for setting
            // the right HTTP Cache headers for the final response.
            if (!filterContext.IsChildAction && !SkipBecauseOfCustom(filterContext))
            {
                CacheHeadersHelper.SetCacheHeaders(filterContext.HttpContext.Response, CacheSettings);
            }
        }

        /// <summary>
        /// Builds the cache settings.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="System.Web.HttpException">
        /// The 'duration' attribute must have a value that is greater than or equal to zero.
        /// </exception>
        protected CacheSettings BuildCacheSettings()
        {
            CacheSettings cacheSettings;

            if (string.IsNullOrEmpty(CacheProfile))
            {
                cacheSettings = new CacheSettings
                {
                    IsCachingEnabled = CacheSettingsManager.IsCachingEnabledGlobally,
                    Duration = Duration,
                    VaryByCustom = VaryByCustom,
                    VaryByParam = VaryByParam ?? "*",
                    IgnoreParamRegexes = IgnoreParamRegexes,
                    PrimaryKeyComponents = PrimaryKeyComponents,
                    Location = (int)Location == -1 ? OutputCacheLocation.Server : Location,
                    NoStore = NoStore,
                    Options = Options,
                };
            }
            else
            {
                var cacheProfile = CacheSettingsManager.RetrieveOutputCacheProfile(CacheProfile);

                cacheSettings = new CacheSettings
                {
                    IsCachingEnabled = CacheSettingsManager.IsCachingEnabledGlobally && cacheProfile.Enabled,
                    Duration = Duration == -1 ? cacheProfile.Duration : Duration,
                    VaryByCustom = VaryByCustom ?? cacheProfile.VaryByCustom,
                    VaryByParam = VaryByParam ?? cacheProfile.VaryByParam ?? "*",
                    IgnoreParamRegexes = IgnoreParamRegexes,
                    PrimaryKeyComponents = PrimaryKeyComponents,
                    Location = (int)Location == -1 ? ((int)cacheProfile.Location == -1 ? OutputCacheLocation.Server : cacheProfile.Location) : Location,
                    NoStore = _noStore.HasValue ? _noStore.Value : cacheProfile.NoStore,
                    Options = Options,
                };
            }

            if (cacheSettings.Duration == -1)
            {
                throw new HttpException("The directive or the configuration settings profile must specify the 'duration' attribute.");
            }

            if (cacheSettings.Duration < 0)
            {
                throw new HttpException("The 'duration' attribute must have a value that is greater than or equal to zero.");
            }

            return cacheSettings;
        }

        /// <summary>
        /// Executes the callback.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="hasErrors">if set to <c>true</c> [has errors].</param>
        private void ExecuteCallback(ControllerContext context, bool hasErrors)
        {
            var cacheKey = KeyGenerator.GenerateKey(context, CacheSettings);

            if (string.IsNullOrEmpty(cacheKey))
            {
                return;
            }

            var callback = context.HttpContext.Items[cacheKey] as Action<bool>;

            callback?.Invoke(hasErrors);
        }
    }
}
