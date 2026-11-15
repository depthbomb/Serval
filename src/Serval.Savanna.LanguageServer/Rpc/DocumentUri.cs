#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Savanna.LanguageServer.Rpc;

public static class DocumentUri
{
    public static string ToPath(string value)
    {
        var uri = new Uri(value);
        if (!uri.IsFile)
        {
            throw new ArgumentException("Savanna supports local file URIs.", nameof(value));
        }

        // Rider percent-encodes the drive colon. Uri.LocalPath treats that as a root-relative path on Windows.
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (OperatingSystem.IsWindows())
        {
            if (uri.Host.Length > 0 && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                path = "//" + uri.Host + path;
            }
            else if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
            {
                path = path[1..];
            }

            path = path.Replace('/', '\\');
        }
        else if (uri.Host.Length > 0 && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Remote file URI hosts are not supported on this platform.", nameof(value));
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A document URI must identify an absolute path.", nameof(value));
        }

        return Path.GetFullPath(path);
    }
}
