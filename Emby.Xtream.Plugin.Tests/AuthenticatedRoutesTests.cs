using System;
using System.Linq;
using Emby.Xtream.Plugin.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Emby makes a route with no attribute require a login but checks no role, so without
    /// <c>Roles = "Admin"</c> any signed-in user can call it, including the routes that delete
    /// content, install updates and restart the server.
    /// </summary>
    public class AuthenticatedRoutesTests
    {
        [Fact]
        public void EveryRoute_RequiresAnAdministrator()
        {
            var routes = typeof(XtreamTunerApi).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(RouteAttribute), false).Length > 0)
                .ToList();

            Assert.NotEmpty(routes);
            var open = routes
                .Where(t => !t.GetCustomAttributes(typeof(AuthenticatedAttribute), false)
                    .Cast<AuthenticatedAttribute>()
                    .Any(a => (a.Roles ?? string.Empty)
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Any(r => string.Equals(r.Trim(), "admin", StringComparison.OrdinalIgnoreCase))))
                .Select(t => t.Name)
                .ToList();

            Assert.True(open.Count == 0, "Routes without [Authenticated(Roles = \"Admin\")]: " + string.Join(", ", open));
        }

        [Fact]
        public void NoRoute_SkipsAuthenticationForLocalRequests()
        {
            // AllowLocal and AllowLocalOnly skip the token check for any device on the local
            // network, which would undo the admin requirement for everyone at home.
            var local = typeof(XtreamTunerApi).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(AuthenticatedAttribute), false)
                    .Cast<AuthenticatedAttribute>()
                    .Any(a => a.AllowLocal || a.AllowLocalOnly))
                .Select(t => t.Name)
                .ToList();

            Assert.True(local.Count == 0, "Routes that allow local requests without a token: " + string.Join(", ", local));
        }
    }
}
