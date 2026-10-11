using System;
using System.Diagnostics;
using System.Globalization;

namespace Ungrep.Bridge
{
    /// <summary>
    /// The loop guard `ug eval` compiles into a snippet (SPEC-editor-link, Running code): each loop
    /// body it compiles starts with <see cref="Check"/>, which throws once the main thread has been
    /// away from the editor loop for the eval's timeout. Public because snippets call it; not an API.
    /// </summary>
    public static class EvalGuard
    {
        private const int Every = 1024;

        private static long _beat = Stopwatch.GetTimestamp();
        private static int _calls;

        /// <summary>Throws a <see cref="TimeoutException"/> when the editor loop last ran <paramref name="seconds"/> ago or more; looks every 1,024 calls.</summary>
        public static void Check(double seconds)
        {
            if (++_calls % Every != 0)
            {
                return;
            }

            double away = (Stopwatch.GetTimestamp() - _beat) / (double)Stopwatch.Frequency;
            if (away >= seconds)
            {
                throw new TimeoutException("a loop kept the editor waiting for " + seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s; ug stopped it");
            }
        }

        /// <summary>The editor loop ran: Bridge calls it on each editor update; tests set an older time.</summary>
        internal static void Beat(long timestamp = 0)
        {
            _beat = timestamp != 0 ? timestamp : Stopwatch.GetTimestamp();
            _calls = 0;
        }
    }
}
