using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>
/// The validity of a server certificate is shown the way the plugin shows
/// every date (yyyy-MM-dd), not in the format of Windows' regional settings,
/// which gave "19.09.2026" beside English texts.
/// </summary>
public class CertificateTextTests
{
    [Fact]
    public void Validity_is_given_as_ISO_dates()
    {
        var german = L.German;
        try
        {
            L.German = false;
            Assert.Equal("2026-09-19 to 2027-09-19",
                OpcUaPlugin.Validity(new DateTime(2026, 9, 19, 10, 0, 0), new DateTime(2027, 9, 19, 10, 0, 0), new DateTime(2026, 11, 9)));
        }
        finally { L.German = german; }
    }

    [Fact]
    public void An_expired_certificate_says_so()
    {
        var german = L.German;
        try
        {
            L.German = false;
            Assert.Equal("2024-01-01 to 2025-01-01  (expired)",
                OpcUaPlugin.Validity(new DateTime(2024, 1, 1), new DateTime(2025, 1, 1), new DateTime(2026, 11, 9)));
        }
        finally { L.German = german; }
    }
}
