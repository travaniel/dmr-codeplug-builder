using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeplugBuilder.Core
{
    /// <summary>
    /// What this program promised RepeaterBook in its API application: who it is, how it credits RepeaterBook, and the
    /// numeric limits on requests. There is no API client yet (no token, and the response format hasn't been seen); when
    /// one is added it must use these values and <see cref="RequestBudget"/>, and each user's own token (never a shared one).
    /// </summary>
    public static class RepeaterBookApi
    {
        /// <summary>The User-Agent registered with RepeaterBook. Must match the approved value exactly.</summary>
        public const string UserAgent = "W6OZZ-CPS/1.3 (+https://github.com/travaniel/dmr-codeplug-builder)";
        public const string TokenHeader = "X-RB-App-Token";
        public const string SiteUrl = "https://www.repeaterbook.com";
        /// <summary>Shown wherever RepeaterBook data is shown or delivered, with a link to <see cref="SiteUrl"/>.</summary>
        public const string Attribution = "Data courtesy of RepeaterBook.com.";

        /// <summary>One request at a time, at least this many seconds apart.</summary>
        public const double MinSecondsBetweenRequests = 2;
        /// <summary>States fetched by one click of the user.</summary>
        public const int MaxStatesPerAction = 5;
        /// <summary>Requests per installation in any rolling hour.</summary>
        public const int MaxRequestsPerHour = 30;
        public const int MaxPagesPerState = 10;
        /// <summary>First wait after a 429 without a Retry-After; doubles each time it repeats.</summary>
        public const int DefaultBackoffSeconds = 60;
        public const int MaxBackoffSeconds = 3600;

        /// <summary>Seconds to wait after a 429: the server's Retry-After, else the previous wait doubled (first one 60), at most an hour.</summary>
        public static int BackoffSeconds(int previousSeconds, int? retryAfterSeconds)
        {
            int s = retryAfterSeconds ?? (previousSeconds <= 0 ? DefaultBackoffSeconds : previousSeconds * 2);
            return Math.Max(1, Math.Min(MaxBackoffSeconds, s));
        }
    }

    /// <summary>
    /// Keeps requests within <see cref="RepeaterBookApi.MinSecondsBetweenRequests"/> and
    /// <see cref="RepeaterBookApi.MaxRequestsPerHour"/>. Pass the saved history in (so the hourly cap survives restarts)
    /// and save <see cref="History"/> after each <see cref="Record"/>. Pure: the caller supplies the clock.
    /// </summary>
    public sealed class RequestBudget
    {
        readonly List<DateTime> sent;

        public RequestBudget(IEnumerable<DateTime> history = null)
        {
            sent = (history ?? Enumerable.Empty<DateTime>()).OrderBy(d => d).ToList();
        }

        /// <summary>Requests still remembered (the last hour matters; older ones are dropped on <see cref="Record"/>).</summary>
        public IReadOnlyList<DateTime> History => sent;

        /// <summary>How long to wait before the next request may go out (zero when it may go now).</summary>
        public TimeSpan WaitBeforeNext(DateTime now)
        {
            var wait = TimeSpan.Zero;
            if (sent.Count > 0)
            {
                var gap = sent[sent.Count - 1].AddSeconds(RepeaterBookApi.MinSecondsBetweenRequests) - now;
                if (gap > wait) wait = gap;
            }
            var recent = sent.Where(d => d > now.AddHours(-1)).ToList();
            if (recent.Count >= RepeaterBookApi.MaxRequestsPerHour)
            {
                // The hourly cap frees up when the oldest of the last MaxRequestsPerHour requests is an hour old.
                var frees = recent[recent.Count - RepeaterBookApi.MaxRequestsPerHour].AddHours(1) - now;
                if (frees > wait) wait = frees;
            }
            return wait;
        }

        /// <summary>True when a request would break the hourly cap (waiting a few seconds won't help).</summary>
        public bool HourlyCapReached(DateTime now)
        {
            return sent.Count(d => d > now.AddHours(-1)) >= RepeaterBookApi.MaxRequestsPerHour;
        }

        public void Record(DateTime now)
        {
            sent.Add(now);
            sent.RemoveAll(d => d <= now.AddHours(-1));
        }
    }
}
