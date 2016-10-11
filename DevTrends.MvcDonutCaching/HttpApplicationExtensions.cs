using System;
using System.Web;

namespace DevTrends.MvcDonutCaching
{
    public static class HttpApplicationExtensions
    {
        public const string SkipByCustomApplicationStateKey = "DonutCachingSkipByCustomKey";
        public const string DurationByCustomKey = "DonutCachingDurationByCustomKey";

        public static void SetSkipOutputCacheByCustomStringDelegate(this HttpApplication application, Func<HttpContextBase, string, bool> func)
        {
            try
            {
                application.Application.Lock();
                application.Application[SkipByCustomApplicationStateKey] = func;
            }
            finally
            {
                application.Application.UnLock();
            }
        }

        public static Func<HttpContext, string, bool> GetSkipOutputCacheByCustomDelegate(this HttpApplication application)
        {
            return application.Application[SkipByCustomApplicationStateKey] as Func<HttpContext, string, bool>;
        }

        public static void SetOutputCacheDurationByCustomStringDelegate(this HttpApplication application, Func<HttpContextBase, string, int?> func)
        {
            try
            {
                application.Application.Lock();
                application.Application[DurationByCustomKey] = func;
            }
            finally
            {
                application.Application.UnLock();
            }
        }

        public static Func<HttpContext, string, int?> GetOutputCacheDurationByCustomDelegate(this HttpApplication application)
        {
            return application.Application[DurationByCustomKey] as Func<HttpContext, string, int?>;
        }
    }
}