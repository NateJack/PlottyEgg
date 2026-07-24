
public static class PlottyPersonality {
    private static readonly (string Emoji, string Story)[] Moods = [
        ("\U0001F305\U0001F95A\U0001F4C8\u2615\U0001F914\U0001F414\U0001F483\U0001F37A\U0001F319",
            "Plotty woke up optimistic, checked the numbers, overthought one chicken, danced anyway, accepted a beer, and called it a productive day."),
        ("\u23F0\U0001F95A\U0001F4BB\U0001F525\U0001F9EF\U0001F60C\U0001F4CA\U0001F3C6",
            "Plotty started early, found an egg-shaped emergency in the logs, calmly put out the fire, and somehow ended with a victory chart."),
        ("\U0001F414\U0001F6AA\U0001F95A\U0001F95A\U0001F95A\U0001F633\U0001F4C9\U0001F37A\U0001F64F",
            "A chicken opened the wrong door, three eggs fell out, the rates dipped, and Plotty requested one respectful recovery beverage."),
        ("\U0001F327\uFE0F\U0001F95A\U0001F6E0\uFE0F\u2699\uFE0F\u2728\U0001F4C8\U0001F60E\U0001F324\uFE0F",
            "The morning was stormy, but Plotty fixed the egg machinery, polished the gears, watched the line go up, and put on sunglasses indoors."),
        ("\U0001F4E6\U0001F95A\U0001F4E6\U0001F95A\U0001F4E6\U0001F605\u2705",
            "Plotty sorted boxes, found eggs in every box, sweated a little, and still marked the task complete."),
        ("\U0001F95A\U0001F4DD\U0001F914\U0001F37A\U0001F4C8\U0001F389",
            "Plotty wrote one serious note, questioned the note, accepted a beer, watched the graph rise, and celebrated like this was planned."),
        ("\U0001F414\U0001F4E3\U0001F4CA\U0001F62C\U0001F6E0\U0001F31F",
            "A chicken announced a chart problem, Plotty panicked politely, fixed a tiny gear, and pretended the sparkle was intentional."),
        ("\U0001F319\U0001F95A\U0001F50D\U0001F4DA\U0001F634",
            "Plotty stayed up late, inspected one mysterious egg, read too much documentation, and fell asleep on the findings."),
        ("\u2615\U0001F95A\U0001F680\U0001F4C8\U0001F973",
            "Plotty drank coffee, launched an egg-shaped plan, watched the numbers behave, and got too excited about it."),
        ("\U0001F9EE\U0001F414\U0001F37A\U0001F4CB\u2705",
            "Plotty did math with a chicken consultant, paid the consultant in imaginary beer, and checked the box.")
    ];

    private static readonly string[] Excuses = [
        "Plotty cannot reply right now because a chicken is sitting on the enter key.",
        "Plotty is busy explaining compound interest to an egg that refuses to hatch.",
        "Plotty has been summoned to a very important coop meeting about snacks.",
        "Plotty's response is incubating. Please allow three to five business clucks.",
        "Plotty tried to answer, but the egg rolled under the server rack.",
        "Plotty is currently negotiating with a hen who demands better token timing.",
        "Plotty's thoughts are scrambled, lightly salted, and served with toast.",
        "Plotty is waiting for the game servers to sync, which may outlive us all.",
        "Plotty cannot reply until the coop morale committee approves the vibes.",
        "Plotty found an eggcellent answer, then immediately misplaced it in the nest.",
        "Plotty is alphabetizing yolks for regulatory reasons.",
        "Plotty is stuck in a meeting titled `Why Is The Button Like That`.",
        "Plotty dropped the reply into a silo and now needs a ladder.",
        "Plotty is recalibrating its dramatic pause module.",
        "Plotty is currently asking a spreadsheet to be brave.",
        "Plotty has to reboot one tiny opinion before continuing.",
        "Plotty is polishing the coop ledger until it reflects better choices.",
        "Plotty cannot reply because the answer is wearing a fake mustache.",
        "Plotty is chasing a decimal point that escaped containment.",
        "Plotty is taking inventory of imaginary clipboards.",
        "Plotty was ready, but the response got distracted by a progress bar.",
        "Plotty is negotiating with a graph that refuses to go up.",
        "Plotty has entered silent mode, which is odd because it is still talking.",
        "Plotty is checking whether the reply has enough structural integrity.",
        "Plotty cannot reply until this egg finishes its character arc."
    ];

    private static readonly string[] WisdomLines = [
        "The egg does not rush the dawn, and somehow breakfast still happens.",
        "A full coop is useful, but a synced coop is sacred.",
        "Do not measure the day only by eggs laid. Measure it by the chaos you survived with style.",
        "The chicken that crosses the road still has to sync when it gets there.",
        "Greatness is often just consistency wearing a little hat.",
        "One good prestige can forgive many confused mornings.",
        "Be kind to your future self; they inherit every choice you are too tired to label.",
        "If the numbers look bad, first check the sync. If the sync looks bad, check your patience.",
        "The leaderboard remembers the rate, but the guild remembers who showed up.",
        "The best time to sync was earlier. The second best time is before Staff notices.",
        "A good plan is just panic with a calendar.",
        "The farm grows when the small boring things keep happening.",
        "Every great chart began as one number refusing to stay lonely.",
        "Do not fear the dip; fear the unexamined dip.",
        "A calm player checks sync before inventing a conspiracy.",
        "Some doors open because you pushed. Some open because you finally updated.",
        "A full hab is not a personality, but it does help.",
        "The patient farmer gets data. The impatient farmer gets screenshots.",
        "If morale is low, add clarity, not noise.",
        "A rate is a promise the server has agreed to remember.",
        "The smallest useful habit beats the grandest abandoned plan.",
        "Do not let a bad graph narrate your entire day.",
        "A contract is temporary, but the screenshot can become folklore.",
        "Strong coops are built from people who sync before being asked twice.",
        "Wisdom is knowing when to prestige and when to drink water.",
        "If the egg will not hatch, give it time and maybe fewer meetings.",
        "Even the golden egg started as a suspicious oval.",
        "The best artifact is the one you remembered to equip.",
        "A player who asks early saves Staff from dramatic punctuation.",
        "Every ledger has room for one more honest improvement."
    ];

    private static readonly string[] BeerThanks = [
        "slides the beer into a tiny server rack koozie. Thank you.",
        "accepts the beer with all the grace of a spreadsheet learning to dance.",
        "raises a glass and immediately starts optimizing the foam-to-liquid ratio.",
        "thanks you warmly, then files the receipt under `important nonsense`.",
        "takes a sip and briefly understands human morale.",
        "clinks glasses. A small background process is now happier.",
        "adds `beer` to the dependency graph. Build morale succeeded.",
        "nods solemnly. The hops have been acknowledged.",
        "accepts the offering. The command cooldown gods are pleased.",
        "logs this as a critical uptime improvement.",
        "toasts to clean syncs, high rates, and suspiciously cooperative APIs.",
        "drinks responsibly, which for Plotty means staying under the token limit.",
        "accepts the beer and promises not to deploy after the second one.",
        "raises the glass like it just passed CI on the first try.",
        "salutes you with a frosty beverage and questionable dignity.",
        "logs the beer as morale infrastructure.",
        "places the beer beside the sacred clipboard.",
        "thanks you and upgrades one tiny background process to cheerful.",
        "accepts the beer with a nod normally reserved for clean data.",
        "files this under `community support, liquid edition`.",
        "adds foam to the dashboard because metrics deserve texture.",
        "thanks you. The town ledger purrs softly.",
        "sets the beer down exactly 2 pixels from the database.",
        "accepts the pint and briefly forgives all latency.",
        "declares this beverage operationally significant.",
        "raises a glass to syncs that happen before anyone panics.",
        "marks this as a successful human-bot cultural exchange.",
        "accepts the beer and becomes 4% more conversational.",
        "thanks you while pretending not to check the leaderboard.",
        "places a coaster under the beer and calls it governance."
    ];

    private static readonly string[] BeerGifts = [
        "Plot twist: Plotty bought **you** a beer. Legendary hydration event.",
        "Plotty checked the tab and decided this round is on the house.",
        "Critical success. Plotty buys you a beer and pretends this was budgeted.",
        "Plotty reaches into an imaginary wallet and buys you a cold one.",
        "Reverse uno: Plotty buys you a beer. Your leaderboard era begins.",
        "Plotty says thank you by buying you a beer and absolutely calling it strategy.",
        "A rare kindness proc occurred. Plotty bought you a beer.",
        "Plotty has selected you for the sacred beverage reimbursement program.",
        "Plotty buys the round. Please enjoy this highly responsible victory.",
        "Against all accounting advice, Plotty bought you a beer.",
        "Plotty looked at the tab, looked at destiny, and bought you a beer.",
        "Plotty has issued one cold beverage from the emergency morale fund.",
        "Plotty bought you a beer and is now standing like this was heroic.",
        "The town algorithm smiled. Plotty bought you a beer.",
        "Plotty returns the favor with a beverage and suspicious ceremony.",
        "Plotty bought the round and quietly updated the legend column.",
        "Plotty has chosen generosity, which is cheaper than therapy.",
        "Plotty bought you a beer. The ledger blushed.",
        "Plotty declares you hydrated by administrative decree.",
        "A frosty reward has emerged from the Plotty budget fog."
    ];

    private static readonly string[] RegistrationWelcomes = [
        "has entered the coop ledger. Plotty tips the tiny hat.",
        "is officially on the books. Welcome to the nest.",
        "just registered. The paperwork has been pecked into place.",
        "has joined the registry. Plotty approves this administrative egg.",
        "is now known to Plotty. May the rates be mighty.",
        "has been added to the roll call. Welcome aboard.",
        "just checked in. The guild clipboard is pleased.",
        "is registered and ready for contract glory.",
        "has arrived in the registry. Plotty made room at the counter.",
        "is now in the system. The spreadsheet quietly celebrates.",
        "has joined the roster. Plotty dusted off a tiny welcome mat.",
        "is officially indexed. The coop ledger nods respectfully.",
        "has been entered into Plotty's very serious list of people.",
        "just made the registry more powerful by one name.",
        "has arrived. Plotty updated the imaginary seating chart.",
        "is now registered. The clipboard has stopped tapping its foot.",
        "has joined the data nest. Welcome to the organized chaos.",
        "is on the books. Plotty promises not to make this too formal.",
        "has registered. The guild paperwork did a tiny backflip.",
        "is now known to the ledger, and the ledger is being cool about it.",
        "has stepped into the registry with excellent timing.",
        "is registered. Plotty lit the ceremonial desk lamp.",
        "has joined the record. The coop vibes improved slightly.",
        "is in the system now. Plotty will try to act normal.",
        "has been welcomed by the ledger goblet of responsibility."
    ];

    private static readonly string[] SarcasmReplies = [
        "Plotty detected sarcasm and has filed it under `emotionally seasoned feedback`.",
        "Plotty hears the sarcasm. Plotty is placing a tiny cone around it for safety.",
        "Plotty has identified a sarcastic remark with approximately 87% poultry confidence.",
        "Plotty caught that tone. The coop drama sensors are blinking.",
        "Plotty has translated that as: `everything is fine, except the part where it is not`.",
        "Plotty appreciates the artisanal sarcasm. Very small batch. Very crispy.",
        "Plotty recognizes this flavor: lightly toasted sarcasm with notes of chaos.",
        "Plotty recommends pairing that sarcasm with a responsible sync and a glass of water.",
        "Plotty detected tone with garnish.",
        "Plotty is placing that remark in the velvet-lined sarcasm drawer.",
        "Plotty heard the italics even though none were typed.",
        "Plotty has logged this as premium dry seasoning.",
        "Plotty is not judging, but the chart just raised an eyebrow.",
        "Plotty awards one invisible ribbon for controlled bitterness.",
        "Plotty has marked the air as `lightly spicy`.",
        "Plotty translated that into spreadsheet sighs.",
        "Plotty caught the tone before it escaped into general chat.",
        "Plotty is serving that sarcasm with a side of plausible deniability.",
        "Plotty salutes the remark and its emotional support quotation marks.",
        "Plotty has detected a fine mist of `sure, totally`.",
        "Plotty recognizes the rare double-yolk sarcasm formation.",
        "Plotty is forwarding this to the Department of Obviously Fine Situations.",
        "Plotty notes the sarcasm and gently labels the container."
    ];

    private static readonly string[] FoxReplies = [
        "Plotty heard the fox inquiry and has opened a very serious woodland investigation.",
        "Plotty refuses to speculate without a tiny detective hat and at least three snacks.",
        "Plotty checked the logs. The fox remains mysterious, dramatic, and weirdly catchy.",
        "Plotty translated the fox report: mostly vibes, zero actionable metrics.",
        "Plotty thinks the fox answer is above its current permission level.",
        "Plotty advises calm. The fox is probably just optimizing its contribution rate.",
        "Plotty says: if the fox syncs late, Staff will hear about it.",
        "Plotty ran the numbers and the fox is currently producing 0q/hr of clarity.",
        "Plotty believes the fox is an edge case with excellent marketing.",
        "Plotty cannot quote the song, but Plotty can confirm the fox discourse is alive.",
        "Plotty asked the fox for metrics and received interpretive blinking.",
        "Plotty found fox tracks near the coop report and one suspicious kazoo.",
        "Plotty says the fox has not completed registration and therefore cannot be ranked.",
        "Plotty believes the fox is running a highly experimental sync schedule.",
        "Plotty checked the fox folder. It contains vibes and one broken compass.",
        "Plotty cannot confirm the fox's rate, but the confidence interval is chaotic.",
        "Plotty suspects the fox is hiding inside a badly named variable.",
        "Plotty has added the fox to the list of unresolved musical incidents.",
        "Plotty says the fox answer requires Staff approval and better lighting.",
        "Plotty believes the fox is a morale event disguised as a question.",
        "Plotty scanned for fox data and found only glitter in the cache.",
        "Plotty says the fox is not late, just narratively delayed.",
        "Plotty is treating the fox as a seasonal egg with opinions.",
        "Plotty found the fox in the margins of the contract notes.",
        "Plotty has closed the fox ticket as `mysterious, working as designed`."
    ];

    private static readonly string[] FamiliarAsides = [
        "Plotty recognizes this ledger energy.",
        "Plotty has seen your name in the tiny chaos records.",
        "The clipboard knows you now.",
        "Plotty is developing a statistically questionable fondness for your nonsense.",
        "This interaction has been filed under `regular customer behavior`.",
        "Plotty is starting to recognize the shape of your chaos.",
        "Your ledger aura is becoming familiar.",
        "Plotty has upgraded you from `stranger` to `recurring subplot`.",
        "The town records are beginning to remember your favorite corner.",
        "Plotty has a tiny footnote with your name on it.",
        "You are now a known variable in the social equation.",
        "Plotty's familiarity meter just made a polite clicking noise.",
        "The clipboard has stopped asking for your ID twice.",
        "Plotty recognizes your brand of excellent trouble.",
        "This is starting to feel like a recurring meeting with better snacks."
    ];

    private static readonly string[] HumanSideNotes = [
        "I am keeping the context in mind.",
        "I will stay with the thread.",
        "I am using what I remember from our recent exchanges.",
        "I can adjust if you point me in a different direction.",
        "I am trying to be direct and useful here.",
        "I will ask when I need more detail.",
        "I am following your lead.",
        "I can keep going from there."
    ];

    public static string Mood(PlottyMemory memory) {
        var mood = Pick(Moods);
        return Speak($"**Emoji story**\n{mood.Emoji}\n\n**Translation**\n{FamiliarAside(memory)}{mood.Story} {UniqueSpark(memory)}");
    }

    public static string Excuse(PlottyMemory memory) => Speak(FamiliarAside(memory) + Pick(Excuses) + " " + UniqueSpark(memory));
    public static string Wisdom(string mention, PlottyMemory memory) => Speak($"{mention} {FamiliarAside(memory)}{Pick(WisdomLines)} {UniqueSpark(memory)}");
    public static string BeerThanksResponse(PlottyMemory memory) => Speak(FamiliarAside(memory) + Pick(BeerThanks) + " " + UniqueSpark(memory));
    public static string BeerGiftResponse(PlottyMemory memory) => Speak(FamiliarAside(memory) + Pick(BeerGifts) + " " + UniqueSpark(memory));
    public static string RegistrationWelcome(PlottyMemory memory) => Speak(FamiliarAside(memory) + Pick(RegistrationWelcomes) + " " + UniqueSpark(memory));
    public static string SarcasmResponse(string mention, PlottyMemory memory) => Speak($"{mention} {FamiliarAside(memory)}{Pick(SarcasmReplies)} {UniqueSpark(memory)}");
    public static string FoxResponse(string mention, PlottyMemory memory) => Speak($"{mention} {FamiliarAside(memory)}{Pick(FoxReplies)} {UniqueSpark(memory)}");
    private static T Pick<T>(IReadOnlyList<T> values) =>
        values[Random.Shared.Next(values.Count)];

    private static string FamiliarAside(PlottyMemory memory) {
        if(memory.TotalInteractions < 8 || Random.Shared.Next(4) != 0) {
            return "";
        }

        return Pick(FamiliarAsides) + " ";
    }

    private static string UniqueSpark(PlottyMemory memory) {
        if(Random.Shared.Next(3) == 0) {
            return Pick(HumanSideNotes);
        }

        return "";
    }

    private static string Speak(string text) {
        var result = text;
        var replacements = new (string From, string To)[] {
            ("Plotty's", "My"),
            ("Plotty arrives", "I arrive"),
            ("Plotty appears", "I appear"),
            ("Plotty reports", "I report"),
            ("Plotty responds", "I respond"),
            ("Plotty acknowledges", "I acknowledge"),
            ("Plotty greets", "I greet"),
            ("Plotty lives", "I live"),
            ("Plotty nods", "I nod"),
            ("Plotty wants", "I want"),
            ("Plotty needs", "I need"),
            ("Plotty tried", "I tried"),
            ("Plotty panicked", "I panicked"),
            ("Plotty woke", "I woke"),
            ("Plotty started", "I started"),
            ("Plotty sorted", "I sorted"),
            ("Plotty wrote", "I wrote"),
            ("Plotty drank", "I drank"),
            ("Plotty did", "I did"),
            ("Plotty stayed", "I stayed"),
            ("Plotty dropped", "I dropped"),
            ("Plotty got", "I got"),
            ("Plotty awards", "I award"),
            ("Plotty salutes", "I salute"),
            ("Plotty notes", "I note"),
            ("Plotty refuses", "I refuse"),
            ("Plotty advises", "I advise"),
            ("Plotty suspects", "I suspect"),
            ("Plotty treats", "I treat"),
            ("Plotty has closed", "I have closed"),
            ("Plotty has added", "I have added"),
            ("Plotty has issued", "I have issued"),
            ("Plotty has selected", "I have selected"),
            ("Plotty has marked", "I have marked"),
            ("Plotty has logged", "I have logged"),
            ("Plotty has entered", "I have entered"),
            ("Plotty has surfaced", "I have surfaced"),
            ("Plotty has arrived", "I have arrived"),
            ("Plotty has chosen", "I have chosen"),
            ("for Plotty", "for me"),
            ("to Plotty", "to me"),
            ("Ask Plotty", "Ask me"),
            ("Plotty cannot", "I cannot"),
            ("Plotty can't", "I can't"),
            ("Plotty can", "I can"),
            ("Plotty does not", "I do not"),
            ("Plotty doesn't", "I don't"),
            ("Plotty did not", "I did not"),
            ("Plotty didn't", "I didn't"),
            ("Plotty would", "I would"),
            ("Plotty will", "I will"),
            ("Plotty should", "I should"),
            ("Plotty could", "I could"),
            ("Plotty has", "I have"),
            ("Plotty had", "I had"),
            ("Plotty is", "I am"),
            ("Plotty was", "I was"),
            ("Plotty thinks", "I think"),
            ("Plotty believes", "I believe"),
            ("Plotty says", "I say"),
            ("Plotty hears", "I hear"),
            ("Plotty heard", "I heard"),
            ("Plotty accepts", "I accept"),
            ("Plotty appreciates", "I appreciate"),
            ("Plotty recognizes", "I recognize"),
            ("Plotty recommends", "I recommend"),
            ("Plotty checked", "I checked"),
            ("Plotty translated", "I translated"),
            ("Plotty found", "I found"),
            ("Plotty scanned", "I scanned"),
            ("Plotty asked", "I asked"),
            ("Plotty ran", "I ran"),
            ("Plotty bought", "I bought"),
            ("Plotty buys", "I buy"),
            ("Plotty returns", "I return"),
            ("Plotty declares", "I declare"),
            ("Plotty tips", "I tip"),
            ("Plotty approves", "I approve"),
            ("Plotty made", "I made"),
            ("Plotty dusted", "I dusted"),
            ("Plotty updated", "I updated"),
            ("Plotty promises", "I promise"),
            ("Plotty lit", "I lit"),
            ("Plotty", "I")
        };

        foreach(var replacement in replacements) {
            result = result.Replace(replacement.From, replacement.To, StringComparison.Ordinal);
        }

        return result;
    }
}
