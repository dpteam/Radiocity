using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Radiocity.Web
{
    /// <summary>
    /// Serves the single-page UI.
    ///
    /// The page is embedded into the executable as a resource rather than shipped
    /// as a loose file, so `Radiocity.exe` stays a single artefact you can copy to
    /// an XP machine and run. build.cmd passes /resource: to csc.
    /// </summary>
    public static class Page
    {
        private const string ResourceName = "Radiocity.web.index.html";

        private static string _cached;

        public static HttpResponse Index()
        {
            string html = Load();

            HttpResponse r = new HttpResponse();
            r.ContentType = "text/html; charset=utf-8";
            r.Body = Encoding.UTF8.GetBytes(html);
            r.Headers["Cache-Control"] = "no-cache";
            return r;
        }

        private static string Load()
        {
            if (_cached != null) return _cached;

            Assembly asm = Assembly.GetExecutingAssembly();
            Stream stream = asm.GetManifestResourceStream(ResourceName);

            if (stream == null)
            {
                // Built without /resource (for example by hand). Say so instead of
                // serving a blank page and leaving someone to guess.
                _cached = "<!doctype html><html><body style=\"font-family:sans-serif;background:#12161e;color:#eee\">" +
                          "<h1>Radiocity</h1><p>The UI resource <code>" + ResourceName + "</code> is not embedded in this build.</p>" +
                          "<p>Build with <code>build.cmd</code>, or check the /resource argument.</p>" +
                          "</body></html>";
                return _cached;
            }

            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                _cached = reader.ReadToEnd();
            }

            return _cached;
        }
    }
}
