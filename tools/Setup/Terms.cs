using System;
using System.IO;

namespace PdfTranslator.Setup
{
    /// <summary>
    /// An optional licence agreement. When build.ps1 finds an EULA.txt beside the
    /// sources it travels in the installer: it is shown in full, has to be accepted
    /// before Install is enabled, and is installed beside the application. Without
    /// one, the installer simply has no agreement step.
    /// </summary>
    internal static class Terms
    {
        public const string FileName = "EULA.txt";

        public static string Text()
        {
            try
            {
                using (Stream stream = Payload.Open(FileName))
                {
                    if (stream != null)
                        using (StreamReader reader = new StreamReader(stream))
                            return reader.ReadToEnd();
                }
            }
            catch (Exception)
            {
            }

            return "";
        }

        /// <summary>True when the installer is carrying an agreement to show.</summary>
        public static bool Present
        {
            get
            {
                using (Stream stream = Payload.Open(FileName)) return stream != null;
            }
        }
    }
}
