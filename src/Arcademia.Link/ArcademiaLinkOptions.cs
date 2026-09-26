using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Arcademia.Link
{
    public sealed class ArcademiaLinkOptions
    {
        public const string ProfileScope = "profile";
        public const string AchievementsScope = "achievements";

        public string ClientId { get; set; }
        public Uri BaseUrl { get; set; } = new Uri("https://manager.arcademia.ac");
        public IList<string> Scopes { get; set; } = new List<string> { ProfileScope, AchievementsScope };
        public string RedirectPath { get; set; } = "/callback";
        public int RedirectPort { get; set; }
        public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public ITokenStore TokenStore { get; set; }
        public string AppName { get; set; }
        public Func<bool, string, string> CallbackPage { get; set; }
        public Func<Uri, CancellationToken, Task> OpenBrowser { get; set; }
        public HttpClient HttpClient { get; set; }
    }
}
