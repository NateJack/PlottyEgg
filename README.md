# Plotty Egg Inc Discord Bot

Plotty is a local Discord bot for an Egg Inc guild. It tracks registered player EIDs, pulls Egg Inc contract/co-op data, summarizes contribution rates, and provides staff tools plus a few pub-style community commands.

The bot is written in C# on .NET 10 and uses Discord.Net plus Google.Protobuf.

## Proto Source

`src/EggContributionBot/Proto/coopstatus.proto` is based on `ei.proto` from [elgranjero/EggIncProtos](https://github.com/elgranjero/EggIncProtos), which is licensed GPL-2.0. The bot adds a C# namespace option so generated classes stay under `EggContribBot.Proto`.

## Current Features

### Player Commands

- `/register-eid` privately registers one or more Egg Inc EIDs to a Discord user.
- Successful registrations post a name-only random welcome in `plotty-gossip`.
- `/contract-late-notify` privately flags that the user will be late joining a contract and keeps them off the 6-hour non-join alert while active.
- `/rates` privately shows the user's running contracts and last 2 completed contracts.
- `/player` shows recent contribution history, registration date, and a refresh button.
- `/eggs-laid` privately shows lifetime eggs laid by farm.
- `/contract-artifacts` privately analyzes artifact inventory and suggests a contract artifact set with artifact image links.
- `/ships` privately shows active ship mission data and can DM the user when a ship returns.
- `/help` answers Egg Inc questions from the Egg Inc Wiki and can use registered backup data for personal questions.

### Contract And Staff Commands

- `/contract` looks up a specific contract/co-op contribution report.
- `/admin-rates-all` shows registered players grouped by contract from lowest to highest contribution.
- `/admin-dashboard` shows staff overview data for registered players, low rates, sync issues, and likely unboosted players.
- `/admin-remove-late-notify` lets staff remove a member's active late notice for one contract or all contracts.
- `/add-demerit` lets staff select a member and add active demerits.
- `/remove-demerit` lets staff select a member and remove active demerits.
- `/demerits-view` lets a member privately view their active demerits.
- `/admin-demerits-view-all` lets staff privately view every member with active demerits.
- `/admin-plotty-speak` lets staff send a message as Plotty.
- 6 hours after a contract starts, Plotty automatically posts a Staff-thread alert listing registered members who have not joined the contract yet.

Admin commands can be run in any channel, but only members with the `Staff` role can use them.

### Community Commands

- `/beer-plotty` lets a member buy Plotty a beer, limited to once per hour.
- `/beer-user` lets members gift beers to each other, limited to one gift per recipient per day.
- `/beerleader` shows the Beer Leaderboard.
- `/plotty-mood` returns Plotty's mood as an emoji story plus a written translation.
- `/plotty-excuses` returns an egg/chicken-themed excuse.
- `/plotty-wisdom` returns random Plotty wisdom.

### Passive Chat Behavior

- Plotty replies when mentioned.
- If Plotty is asked a question by mention, it answers directly when possible; otherwise it gives a general answer and asks a follow-up question.
- Plotty watches for `what the fox` and responds with a fox-themed reply.
- Plotty watches for likely sarcastic remarks and responds.
- Plotty keeps lightweight local personality memory from interactions, storing counters and timestamps only, then uses familiarity to vary future social replies.
- Plotty composes a fresh first-person side note for every personality interaction using rotating voice fragments.

## Local Setup

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. Copy the example settings file:

```powershell
Copy-Item src\EggContributionBot\appsettings.example.json src\EggContributionBot\appsettings.json
```

3. Edit `src\EggContributionBot\appsettings.json`:

```json
{
  "Discord": {
    "Token": "paste-bot-token-here",
    "GuildId": "your-discord-server-id"
  },
  "Storage": {
    "DataPath": "data/egg-links.json",
    "KeyPath": "data/eid-key.bin"
  }
}
```

Never commit `appsettings.json`, `data/`, logs, or build output. They are ignored by `.gitignore`.

## Build

```powershell
dotnet build EggContributionBot.sln
```

## Run Locally

From the project root:

```powershell
.\start-bot.ps1
```

Or double-click:

```text
start-bot.bat
```

The bot stays online only while it is running on this computer.

## Discord Invite

Use the bot application's OAuth2 invite URL with these scopes:

```text
bot applications.commands
```

The bot needs permissions for slash commands, reading messages, sending messages, embeds, and managing private threads if thread features are re-enabled later.

## Project Layout

```text
EggContributionBot.sln
src/EggContributionBot/
  EggIncClient.cs
  Program.cs
  SecureText.cs
  Proto/coopstatus.proto
```

`EGG9000-master/` and `ImportedBot/` are local reference/import folders and are ignored by the repo.
