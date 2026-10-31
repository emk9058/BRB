using System.Diagnostics;

public static class LeagueLockfile
{
    public static LeagueConnection? Connect()
    {
        foreach (var path in CandidatePaths())
        {
            var connection = TryRead(path);
            if (connection is not null)
                return connection;
        }

        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Riot Games",
            "League of Legends",
            "lockfile");

        var installDirectory = FindInstallDirectory();
        if (installDirectory is not null)
            yield return Path.Combine(installDirectory, "lockfile");
    }

    private static string? FindInstallDirectory()
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessesByName("LeagueClientUx").FirstOrDefault();
            var exe = process?.MainModule?.FileName;
            return string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
        }
        catch
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static LeagueConnection? TryRead(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            // League keeps this file open, so it has to be read while sharing access.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var parts = reader.ReadToEnd().Trim().Split(':');
            if (parts.Length < 5 || !int.TryParse(parts[2], out var port))
                return null;

            var password = string.Join(':', parts.Skip(3).Take(parts.Length - 4));
            if (password.Length == 0)
                return null;

            return new LeagueConnection(port, password);
        }
        catch
        {
            return null;
        }
    }
}

public record LeagueConnection(int Port, string Password);
