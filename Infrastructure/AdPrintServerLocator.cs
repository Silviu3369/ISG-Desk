using System.DirectoryServices;
using System.Net.NetworkInformation;

namespace NetScopeDiagnosticCenter.Infrastructure;

/// <summary>
/// Finds print servers a domain publishes in Active Directory by querying the
/// <c>printQueue</c> objects every shared queue creates (List in Directory). This is
/// how "auto-find the print server" works with zero configuration: no subnet scan,
/// no admin rights, one bounded LDAP query against the default naming context.
/// Returns an empty list on workgroup machines or when AD is unreachable — never throws.
/// </summary>
public static class AdPrintServerLocator
{
    /// <summary>Synchronous LDAP query — call through Task.Run from UI code.</summary>
    public static IReadOnlyList<AdPrintServerInfo> FindPublishedPrintServers(int maxServers = 5)
    {
        try
        {
            // Cheap domain-membership check before any LDAP traffic.
            var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (string.IsNullOrWhiteSpace(domain))
            {
                return [];
            }

            using var rootDse = new DirectoryEntry("LDAP://RootDSE");
            var namingContext = rootDse.Properties["defaultNamingContext"].Value?.ToString();
            if (string.IsNullOrWhiteSpace(namingContext))
            {
                return [];
            }

            using var searchRoot = new DirectoryEntry($"LDAP://{namingContext}");
            using var searcher = new DirectorySearcher(searchRoot)
            {
                Filter = "(objectCategory=printQueue)",
                PageSize = 200,
                SizeLimit = 500,
                ClientTimeout = TimeSpan.FromSeconds(6),
                ServerTimeLimit = TimeSpan.FromSeconds(5)
            };
            searcher.PropertiesToLoad.Add("serverName");
            searcher.PropertiesToLoad.Add("printerName");

            var queuesPerServer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var results = searcher.FindAll();
            foreach (SearchResult result in results)
            {
                var server = result.Properties["serverName"] is { Count: > 0 } values
                    ? values[0]?.ToString()
                    : null;
                if (string.IsNullOrWhiteSpace(server))
                {
                    continue;
                }

                var normalized = server.Trim().TrimStart('\\');
                queuesPerServer[normalized] = queuesPerServer.TryGetValue(normalized, out var count) ? count + 1 : 1;
            }

            return queuesPerServer
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxServers))
                .Select(pair => new AdPrintServerInfo(pair.Key, pair.Value))
                .ToList();
        }
        catch
        {
            // Workgroup, broken secure channel, or AD unreachable — auto-detect simply
            // falls back to manual entry; this helper must never break the Printers page.
            return [];
        }
    }
}

/// <summary>A print server published in AD and how many shared queues it publishes.</summary>
public sealed record AdPrintServerInfo(string Server, int QueueCount);
