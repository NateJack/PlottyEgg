# Plotty Egg Inc Discord Bot

Plotty is a local Discord bot for an Egg Inc guild. It tracks registered player EIDs, pulls Egg Inc contract/co-op data, summarizes contribution rates, and provides staff tools plus a few town-style community commands.

The bot is written in C# on .NET 10 and uses Discord.Net plus Google.Protobuf.

## Proto Source

`src/EggContributionBot/Proto/coopstatus.proto` is based on `ei.proto` from [elgranjero/EggIncProtos](https://github.com/elgranjero/EggIncProtos), which is licensed GPL-2.0. The bot adds a C# namespace option so generated classes stay under `EggContribBot.Proto`.

## Current Features

### Player Commands

- `/register-eid` privately registers one or more Egg Inc EIDs to a Discord user.
- Successful registrations post a name-only random welcome in `plotty-questions`.
- `/contract-late-notify` privately flags that the user will be late joining a contract and keeps them off the 6-hour non-join alert while active.
- Posting in `#i-am-late-today` also marks the user late for 48 hours, the same as `/contract-late-notify`.
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
- `/admin-list-members` compares Discord members with Plotty EID registrations, and can also compare against the EGG9000 guild roster API.
- `/admin-e9k-compare` compares EGG9000 guild-tag members with live Discord server members.
- `/admin-ping-unregistered` lets Staff send one custom message pinging members who have not registered with Plotty.
- `/admin-health` privately shows Staff the last successful/failing check-ins for Plotty's background monitors.
- `/admin-remove-late-notify` lets staff remove a member's active late notice for one contract or all contracts.
- `/add-demerit` lets staff select a member and add active demerits.
- `/remove-demerit` lets staff select a member and remove active demerits.
- `/demerits-view` lets a member privately view their active demerits.
- `/admin-demerits-view-all` lets staff privately view every member with active demerits.
- `/admin-plotty-speak` lets staff send a message as Plotty.
- 6 hours after a contract starts, Plotty automatically posts a Staff-thread alert listing registered members who have not joined the contract yet.

Admin commands can be run in any channel, but only members with the `Staff` role can use them.

### Community Commands

- `/beverage-plotty` lets a member buy Plotty a beverage; beer and wine are limited to once per hour.
- `/beverage-user` lets members gift beverages to each other, with an optional recipient ping; beer and wine are limited to once per hour.
- `/beverage-leader` privately shows the Beverage Leaderboard.
- `/plotty-mood` returns Plotty's mood as an emoji story plus a written translation.
- `/plotty-excuses` returns an egg/chicken-themed excuse.
- `/plotty-wisdom` returns random Plotty wisdom.

### Passive Chat Behavior

- Plotty replies conversationally when mentioned and keeps a short, in-memory conversation with each user. Members can continue by replying directly to Plotty's message without mentioning it again.
- Without an OpenAI key, Plotty uses a local no-key reply generator. With `OPENAI_API_KEY`, Plotty uses hosted AI-generated replies.
- Chat redacts EIDs and API-key-looking text, does not receive Plotty's registration database, and hosted AI requests are marked not to be stored.
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
    "GuildId": "your-primary-discord-server-id",
    "GuildIds": [
      "your-primary-discord-server-id",
      "another-discord-server-id"
    ],
    "AdminUserIds": [
      "trusted-discord-user-id"
    ]
  },
  "Storage": {
    "DataPath": "data/egg-links.db",
    "KeyPath": "data/eid-key.bin"
  },
  "Egg9000": {
    "BaseUrl": "https://egg9000.com/",
    "ApiKey": ""
  },
  "OpenAi": {
    "BaseUrl": "https://api.openai.com/v1/",
    "ApiKey": "",
    "Model": "gpt-5-nano"
  }
}
```

Prefer setting `EGG9000_API_KEY` as an environment variable instead of saving the API key in `appsettings.json`.
Plotty can reply to mentions without an OpenAI key by using its local no-key conversation fallback. Set `OPENAI_API_KEY` as an environment variable only if you want hosted generated conversations. The default `gpt-5-nano` model minimizes token cost and can be changed in local settings when stronger conversation quality is more important.

On Windows, securely save a new OpenAI API key for the current user without putting it in a project file:

```powershell
.\set-openai-key.ps1
```

Never commit `appsettings.json`, `data/`, `backups/`, logs, or build output. They are ignored by `.gitignore`.

Plotty stores registrations and bot state in SQLite. On the first startup after upgrading from the older JSON store, it imports `data/egg-links.json` into `data/egg-links.db` exactly once. The original JSON file is retained as a local rollback copy and is no longer modified.

## Build

```powershell
dotnet build EggContributionBot.sln
```

## Tests

The repo includes a small no-dependency console test project for core bot logic:

```powershell
dotnet run --project tests\EggContributionBot.Tests\EggContributionBot.Tests.csproj
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
  Config/
    BotSettings.cs
  Data/
    Egg9000ArtifactData.cs
  Models/
    BotModels.cs
  Services/
    DataStore.cs
    EggWikiClient.cs
    MonitorHealthService.cs
    PlottyAiClient.cs
    PlottyPersonality.cs
  EggIncClient.cs
  Program.cs
  SecureText.cs
  Proto/coopstatus.proto
tests/EggContributionBot.Tests/
  Program.cs
```

`Program.cs` owns Discord startup and command handlers. Models, config, storage, personality, wiki, artifact data, and monitor health are split into dedicated files so new features can be tested and reviewed in smaller pieces.
