using Discord;
using Discord.WebSocket;
using EggContribBot.Services;

namespace EggContribBot.Commands;

class UtilityCommands(DataStore dataStore, EggIncClient eggClient, DiscordSocketClient client)
{
    private readonly DataStore _dataStore = dataStore;

    private readonly EggIncClient _eggClient = eggClient;

    private readonly DiscordSocketClient _client = client;
    public async Task HandleRegisterEidAsync(SocketSlashCommand command) {
        var modal = new ModalBuilder()
            .WithTitle("Register Egg Inc ID")
            .WithCustomId("register-eid-modal")
            .AddTextInput(
                label: "Egg Inc ID",
                customId: "eid",
                style: TextInputStyle.Short,
                placeholder: "EI1234567890123456",
                minLength: 4,
                maxLength: 32,
                required: true)
            .Build();

        await command.RespondWithModalAsync(modal);
    }

    public async Task HandleRegisterEidModalAsync(SocketModal modal) {
        if(modal.GuildId is null) {
            await modal.RespondAsync("Use Plotty inside your Discord server.", ephemeral: true);
            return;
        }

        await modal.DeferAsync(ephemeral: true);

        var eid = EggIncClient.NormalizeEggId(modal.Data.Components.First(c => c.CustomId == "eid").Value);
        var validation = await _eggClient.ValidateEggIdAsync(eid);
        if(!validation.IsValid) {
            await modal.FollowupAsync("Plotty could not validate that EID with Egg Inc. Please double-check it and try again.", ephemeral: true);
            return;
        }

        var eggName = string.IsNullOrWhiteSpace(validation.EggName) ? null : validation.EggName;
        await _dataStore.SaveRegisteredEidAsync(modal.GuildId.Value, modal.User.Id, eid, eggName);

        var accounts = await _dataStore.GetRegisteredAccountsAsync(modal.GuildId.Value, modal.User.Id);
        var suffix = SecureText.Sha256(eid)[..8];
        var parseNote = validation.BackupParseLimited
            ? " Egg Inc returned one malformed optional backup field, so I saved the EID without an Egg Inc display name for now."
            : "";
        await modal.FollowupAsync(
            $"Saved your EID securely and tied it to your Discord name. You now have `{accounts.Count}` EID account(s) registered. Stored hash ending: `{suffix}`.{parseNote}",
            ephemeral: true);

        await SendRegistrationWelcomeAsync(modal.GuildId.Value, modal.User);
    }
    async Task SendRegistrationWelcomeAsync(ulong guildId, IUser user) {
        var guild = _client.GetGuild(guildId);
        var questionsChannel = guild is null ? null : Helpers.FindPlottyQuestionsChannel(guild);
        if(questionsChannel is null) {
            return;
        }

        var displayName = user is SocketGuildUser guildUser ? guildUser.DisplayName : user.Username;
        var memory = await _dataStore.RecordPlottyInteractionAsync(guildId, user.Id, "registration");
        await questionsChannel.SendMessageAsync(
            $"{displayName} {PlottyPersonality.RegistrationWelcome(memory)}",
            allowedMentions: AllowedMentions.None);
    }
}