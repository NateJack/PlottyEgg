(() => {
  function extractArrayAfter(text, marker) {
    const markerIndex = text.indexOf(marker);
    if (markerIndex < 0) return null;
    const start = text.indexOf("[", markerIndex);
    if (start < 0) return null;

    let depth = 0;
    let inString = false;
    let escaped = false;
    for (let i = start; i < text.length; i++) {
      const c = text[i];
      if (inString) {
        if (escaped) escaped = false;
        else if (c === "\\") escaped = true;
        else if (c === "\"") inString = false;
        continue;
      }

      if (c === "\"") {
        inString = true;
      } else if (c === "[") {
        depth++;
      } else if (c === "]") {
        depth--;
        if (depth === 0) return text.slice(start, i + 1);
      }
    }

    return null;
  }

  const url = new URL(location.href);
  const contractId = url.searchParams.get("ContractId") || "";
  const league = Number(url.searchParams.get("League") || "5");
  const scripts = [...document.scripts].map(script => script.textContent || "").join("\n");
  const raw = extractArrayAfter(scripts, "const _activeRaw =");
  if (!raw) {
    throw new Error("Could not find EGG9000 active co-op data on this page.");
  }

  const coops = JSON.parse(raw);
  const players = coops.flatMap(coop => (coop.Users || [])
    .filter(user => user.Name)
    .map(user => ({
      name: String(user.Name || ""),
      guildTag: user.Guild ? String(user.Guild) : null,
      chickens: String(user.NumChickens || ""),
      rate: String(user.Rate || ""),
      projected: String(user.Projected || ""),
      joined: String(user.Status || "").includes("\u2714") || String(user.Status || "").includes("\u2705"),
      coopStatus: String(coop.Status || ""),
      hoursToFinish: coop.HoursToFinish !== null && coop.HoursToFinish !== undefined && Number.isFinite(Number(coop.HoursToFinish)) ? Number(coop.HoursToFinish) : null,
      coopFinished: String(coop.Status || "").toLowerCase() === "finished" ||
        String(coop.Status || "").toLowerCase().startsWith("complete once") ||
        (coop.HoursToFinish !== null && coop.HoursToFinish !== undefined && Number.isFinite(Number(coop.HoursToFinish)) && Number(coop.HoursToFinish) <= 0)
    })));

  const payload = {
    contractId,
    league,
    exportedAt: new Date().toISOString(),
    sourceUrl: location.href,
    players
  };

  const json = JSON.stringify(payload, null, 2);
  const ports = [5199, 57291, 58423, 61997];
  (async () => {
    let lastError = null;
    for (const port of ports) {
      try {
        const response = await fetch(`http://127.0.0.1:${port}/egg9000-scrape/`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: json
        });
        if (!response.ok) throw new Error(await response.text());
        console.log(`Sent ${players.length} EGG9000 players for ${contractId} league ${league} to Plotty's local scrape cache on port ${port}.`);
        return;
      } catch (error) {
        lastError = error;
      }
    }

    copy(json);
    console.warn(`Local receiver unavailable (${lastError?.message || "no response"}). Copied ${players.length} players instead; run tools/save-egg9000-browser-scrape.ps1.`);
  })();
})();
