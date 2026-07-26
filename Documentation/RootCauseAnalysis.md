# Teaching Modern Dev Tools about your mod's errors

*A guide for mod authors: make the debug log explain **why** your error happened — in plain language,
with a fix — so players understand the root cause before they open a bug report.*

Modern Dev Tools (packageId `astryl.ModernDevTools`, namespace `ModernDevTools`) traces every RimWorld
error back to the mod that likely caused it and shows a **"What this means"** section: a plain-language
explanation and a suggested fix. Anyone can add entries to that section for their own mod, so a confusing
`NullReferenceException` becomes *"Your save was made before you added Mod X; load a save from after, or
start a new colony."*

## The fastest way: ship a file

**Drop one JSON file in your mod and Modern Dev Tools reads it — no code, no dependency, no risk.**

```
<YourMod>/About/known-issues.json
```

It uses the **exact same schema as the community bug database**, so you learn one format and can copy
entries between your mod and the community repo freely. If a player doesn't have Modern Dev Tools, the
file just sits there doing nothing — safe to ship to everyone.

## How the layers stack

When a player selects an error, these sources are all consulted and the best matches are shown, highest
score first:

1. **Your mod's shipped `About/known-issues.json`** — always on, ranks highest (it's the author's own answer).
2. **The community bug database** (`known-issues.json` on GitHub) — opt-in; the **fallback** when your
   mod ships no file, or its file doesn't match.
3. **The shipped knowledge library** — generic explanations (null reference, version mismatch, …).
4. **Declared incompatibilities** — read automatically from every active mod's `About.xml` (see the end).

So: ship a file for *your* errors; the community repo covers everything else.

---

## The matching model (shared everywhere)

An entry declares **when it applies** (the `match` signals) and **what to say** (title, explanation, fix).
Every entry is scored against the selected error; higher score shows first.

### Match signals

| Signal | Field (`match.…`) | Tested against | Score |
| --- | --- | --- | --- |
| Exception type | `exceptionTypes` | short type name in the message (e.g. `NullReferenceException`) | **+3** |
| Regex | `regexes` | full message text (case-insensitive .NET regex) | **+3** |
| Keywords | `keywords` | substring of the message (case-insensitive) | **+2** |
| Namespaces | `namespaces` | namespace prefixes seen in the **stack trace** | **+2** |
| PackageIds | `packageIds` | packageIds of mods the stack trace implicated | **+2** |

Rules of thumb:

- **Any signal you provide is tested; you don't need all of them.** More matching signals = higher score.
- **Be specific.** `exceptionTypes` + a `regex` or `namespace` is precise. A single broad keyword like
  `null` will match half the log and mislead players — don't.
- **`namespaces` and `packageIds` are your strongest targeting** — they tie the entry to *your* code
  appearing in the stack, so it won't fire on someone else's unrelated error.

### Message fields

| Purpose | JSON key | `KnownIssueDef` (XML) |
| --- | --- | --- |
| Short title | `title` | `<label>` |
| Plain-language explanation | `explanation` | `<description>` |
| Suggested fix / what to do | `fix` | `<fix>` |
| Optional help link | `url` | `<url>` |
| Severity hint | `severity` | *(taken from the log entry itself)* |
| Credit | `reportedBy` | *(defaults to your mod's name)* |

---

## Path 1 — Ship `known-issues.json` (recommended)

Create `<YourMod>/About/known-issues.json` (the About folder is ideal — RimWorld ignores extra files there, so it needs no LoadFolders setup and ships harmlessly to players who don't have Modern Dev Tools):

```json
{
  "version": 1,
  "issues": [
    {
      "id": "yourmod-missing-hediff",
      "title": "Your Mod: save is missing a body-part def",
      "explanation": "Your Mod adds a hediff that lives on a body part added by a DLC. This save was made without that DLC, so the part isn't there and the hediff has nowhere to attach.",
      "fix": "Enable the DLC this mod needs, or remove Your Mod before loading this save.",
      "url": "https://github.com/you/yourmod/wiki/known-issues",
      "severity": "error",
      "reportedBy": "you",
      "match": {
        "exceptionTypes": ["NullReferenceException"],
        "keywords": ["YourMod_Hediff"],
        "regexes": [],
        "namespaces": ["YourMod"],
        "packageIds": ["you.yourmod"]
      }
    }
  ]
}
```

That's it — one file in your `About/` folder. No dependency, no LoadFolders, no defs. `reportedBy` defaults
to your mod's name if you leave it out. You can validate against `known-issues.schema.json` in the community
database repo. (`About/ModernDevTools.json` is also accepted if you prefer a namespaced filename.)

---

## Path 2 — Ship a `KnownIssueDef` (XML defs)

Prefer defs if you want your text **translatable via DefInjected**, or you're already writing XML. Same
matching model, expressed as a def. `<label>` = title, `<description>` = explanation.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Defs>
  <ModernDevTools.KnownIssueDef>
    <defName>YourMod_MissingHediff</defName>
    <label>Your Mod: save is missing a body-part def</label>
    <description>Your Mod adds a hediff that lives on a body part added by a DLC. This save was made without that DLC, so the part isn't there and the hediff has nowhere to attach.</description>
    <fix>Enable the DLC this mod needs, or remove Your Mod before loading this save.</fix>
    <url>https://github.com/you/yourmod/wiki/known-issues</url>
    <priority>1</priority>
    <exceptionTypes><li>NullReferenceException</li></exceptionTypes>
    <keywords><li>YourMod_Hediff</li></keywords>
    <namespaces><li>YourMod</li></namespaces>
    <packageIds><li>you.yourmod</li></packageIds>
  </ModernDevTools.KnownIssueDef>
</Defs>
```

Extra def-only fields: `<priority>` (added to the match score), `<ignorable>true</ignorable>` (gives players
an **Ignore** button to mute a harmless class), and `<attributeRegex>` (a regex whose **first capture group**
is a mod name/packageId to blame — the matched mod is added to **Likely source**, e.g.
`<attributeRegex>^Mod (.+?) dependency</attributeRegex>`).

**A `KnownIssueDef` is an unknown def type when Modern Dev Tools isn't installed**, which is a load error.
Gate it with conditional load folders so it only loads when MDT is active. In your mod root,
`LoadFolders.xml`:

```xml
<loadFolders>
  <v1.6>
    <li>/</li>
    <li IfModActive="astryl.ModernDevTools">ModernDevTools</li>
  </v1.6>
</loadFolders>
```

Put the def XML under `ModernDevTools/Defs/…`. (The shipped JSON file in Path 1 needs none of this — prefer
it unless you specifically want DefInjected translation.)

---

## Path 3 — Contribute to the community database

Same entry, shared with **every** Modern Dev Tools user and usable for *any* mod or vanilla. Submit through
the in-game **Submit a fix** button (it pre-fills the match block from the real error) or a **fix
submission** Issue Form on the database repo; a workflow validates it and opens a pull request. Use this for
cross-mod interactions and vanilla quirks; use Path 1 for errors that are unambiguously *your* mod's.

---

## Path 4 — Register an `ErrorModule` (C#, dynamic)

When a static entry isn't enough — you need to inspect the live game, check which *other* mods are loaded,
or compute the explanation — subclass `ModernDevTools.ErrorModule`. For each selected error the engine
calls your module twice, both sandboxed (a throw can't break the log):

```csharp
using ModernDevTools;
using Verse;

public class MyAnalyzer : ErrorModule
{
    public override void Diagnose(ErrorContext ctx)
    {
        // ctx.Text / StackTrace / Frames / ExceptionType / Namespaces / ImplicatedPackageIds
        if (ctx.ExceptionType == "NullReferenceException"
            && ctx.Text != null && ctx.Text.Contains("MyThing")
            && !ModsConfig.IsActive("someauthor.requiredmod"))
        {
            ctx.AddDiagnosis(new ErrorDiagnosis
            {
                Title       = "My Mod needs Required Mod",
                Explanation = "My Mod's thing expects Required Mod's framework, which isn't loaded.",
                Fix         = "Subscribe to and enable Required Mod, then restart RimWorld.",
                Source      = "mymod.requiredmod-missing", // stable id (also the mute key if Ignorable)
                Score       = 8,                            // higher = shown first
            });
        }
    }

    // Add to the "Likely source" list (use the WeightXxx constants so you rank sensibly):
    public override void ContributeAttribution(ErrorContext ctx)
    {
        var meta = ModLister.GetModWithIdentifier("someauthor.mod");
        if (meta != null)
            ctx.AttributeNamed(meta.Name, meta.PackageId, ErrorContext.WeightKnownAttr, "matches a pattern I recognise");
    }
}
```

Register it in C# (from a `[StaticConstructorOnStartup]` or your `Mod` ctor):

```csharp
ModernDevTools.ModernDevToolsAPI.RegisterModule(new MyAnalyzer());
```

…or in XML with an `ErrorModuleDef` pointing at `<workerClass>`, gated on `<requiresPackageId>` /
`<requiresAnyPackageId>` / `<requiresDlc>` so it only loads when relevant. To keep the C# **optional**, ship
the integration assembly in a conditional load folder (the same `IfModActive` trick as Path 2); or if your
module only makes sense with MDT anyway, hard-depend on it in About.xml. Call
`ModernDevToolsAPI.Invalidate()` if you change registrations at runtime.

---

## Automatic: declared incompatibilities from About.xml

You don't have to do anything for this, but it's worth knowing: Modern Dev Tools reads **every active
mod's `About.xml` `<incompatibleWith>`** list. When an error implicates a mod that has declared an
incompatibility with another **active** mod, the log explains it — even if no community database knows
about that conflict. So keeping your `About.xml` `<incompatibleWith>` accurate directly improves the
diagnosis players see:

```xml
<incompatibleWith>
  <li>someauthor.conflictingmod</li>
</incompatibleWith>
```

(Pairs already covered by the community rules are skipped, so there's no double-up.)

---

## Writing explanations players actually understand

The whole point is **root cause before bug report**. Good entries:

- **Name the cause, not the symptom.** Not *"a null reference occurred"* — say *"the save was made before
  you added this mod, so the data it expects isn't there."*
- **Make the fix a concrete action.** *"Load a save from before you removed Mod X,"* not *"resolve the
  conflict."*
- **Say when it's harmless.** If a warning is safe to ignore, say so (and mark it `ignorable`) — that stops
  needless bug reports.
- **Plain language, sentence case, ASCII.** Assume the reader is a player, not a programmer.
- **Target precisely.** Prefer `namespaces` / `packageIds` over broad `keywords`, so your entry only fires
  on genuinely-your errors.

---

## Field reference

### JSON entry / `ModernDevTools.KnownIssueDef`

| JSON | XML | Meaning |
| --- | --- | --- |
| `id` | `defName` | Unique id (also the mute key when ignorable). |
| `title` | `label` | Short title. |
| `explanation` | `description` | Plain-language explanation. |
| `fix` | `fix` | Suggested remedy. |
| `url` | `url` | Optional help link. |
| `severity` | *(from log)* | `error` / `warning` / `info`. |
| `reportedBy` | *(mod name)* | Credit. |
| *(n/a)* | `priority` | Added to match score. |
| *(n/a)* | `ignorable` | Show an **Ignore** button. |
| *(n/a)* | `attributeRegex` | Regex; first group is a mod to blame. |
| `match.exceptionTypes` / `keywords` / `regexes` / `namespaces` / `packageIds` | `<exceptionTypes>` / `<keywords>` / `<regexes>` / `<namespaces>` / `<packageIds>` | Match signals. |

### `ErrorContext` (what a C# module reads/writes)

Read: `Text`, `StackTrace`, `Frames`, `ExceptionType`, `Namespaces`, `ImplicatedPackageIds`, `Message`.
Write: `AddDiagnosis(ErrorDiagnosis)`, `Attribute(...)`, `AttributeNamed(...)`, `AttributeSource(...)`.
Weights: `WeightStackBase`, `WeightMessageOwner`, `WeightMessagePath`, `WeightKnownAttr`,
`WeightMessagePackage`, `WeightMessageName`.

### `ErrorDiagnosis` (a "What this means" card)

`Title`, `Explanation`, `Fix`, `Url`, `Source` (stable id / mute key), `Score` (higher shows first),
`Ignorable`.
