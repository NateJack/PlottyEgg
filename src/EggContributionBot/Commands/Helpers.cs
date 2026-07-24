using Discord.WebSocket;
namespace EggContribBot.Commands;

public class Helpers
{
    public static string GetString(SocketSlashCommand command, string name) =>
        (string)command.Data.Options.First(o => o.Name == name).Value;
    
    public static bool GetBool(SocketSlashCommand command, string name) =>
        command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value is bool value && value;

    public static string FormatEggs(double amount) {
        string[] suffixes = ["", "K", "M", "B", "T", "q", "Q", "s", "S", "o", "N", "d", "U", "D"];
        var value = amount;
        var abs = Math.Abs(value);
        var index = 0;
        while(abs >= 1000 && index < suffixes.Length - 1) {
            value /= 1000;
            abs /= 1000;
            index++;
        }

        var format = abs >= 100 ? "0" : abs >= 10 ? "0.0" : "0.00";
        return value.ToString(format) + suffixes[index];
    }

    public static string NormalizeName(string? value) {
        if(string.IsNullOrWhiteSpace(value)) {
            return "";
        }

        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }
}