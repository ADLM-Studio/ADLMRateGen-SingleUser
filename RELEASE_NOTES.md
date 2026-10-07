# ADLM Rate Gen — Release Notes

## Rate Gen 3.0, build 3.0.2610.2

Everything in build 2610.1, plus Price a bill and Save to ADLM Cloud. Rate Gen
keeps its launch version, 3.0, through 2026; this release moves only the
build number (3.0.2610.2). It was prepared as "3.1.0" and renumbered.

### Price a bill

Open any client's bill of quantities (.xlsx, or .xls on a PC with Excel), in
their own layout. RateGen finds the description, unit and quantity columns, reads
each item with the section and headings above it, and ADLM AI proposes one of
your rates for each measured item, in the same unit only. Nothing is priced until
you accept it: accept one line, accept every match ADLM AI is 85% or more sure of,
or search for another rate. Lines with no good match stay unpriced and are listed.
Totals by section and for the bill. Download Excel saves a copy in the client's
layout with your rates and amounts filled in, a note on each rate naming the
RateGen rate, and a summary sheet; the client's file is never changed. Steel
billed by the kg is offered your per-tonne rates per kg.

Save to ADLM Cloud keeps the bill, priced and unpriced lines, as a RateGen
project in your ADLM account; saving again from the same bill updates it. Each
priced line keeps the RateGen rate that priced it. ICMS export comes in a later
release.

## Rate Gen 3.0, build 3.0.2610.1

Everything in the 3.0 launch release, plus one fix to Saved Rates. This is the build staged on
4 Oct 2026 as "3.0.1", renumbered: Rate Gen keeps its launch version, 3.0,
through 2026 and each release moves only the build number (Major.Minor.YYMM.N).

### Fixed: custom rates made on the website or another PC were deleted

RateGen never downloaded your custom rates. When it synced (after an edit,
at sign-in and at sign-out) it treated every rate in your ADLM account that
was not in this PC's Saved Rates as one you had deleted, and removed it. A
rate built on the website's custom rate builder, or in RateGen on another
PC, disappeared at the next sync; a fresh install removed them all.

Now:

- Rates in your account that this PC does not have are **downloaded** into
  Saved Rates, at sign-in and at every sync, with their own prices, unit and
  section.
- A rate is deleted from your account **only when you delete it here**.
- Only rates you changed here are uploaded. If a rate was edited on the
  website and you have not touched it here, this PC takes the website's
  version.
- A rate missing from your account but still on this PC is uploaded again,
  unless you deleted it in RateGen on another PC.

Not yet: a plant line on a rate built on the website does not show here, as
RateGen has no plant line yet. It is kept in your account.

### Technical detail

- `UserRatesCloudSync` syncs custom rates one by one, cloud first, and no
  longer sends them in the whole-list push. Requests carry
  `X-ADLM-Rates-Sync: 2`; the server (ADLMWebsite #116) honours this
  desktop's explicit deletes and refuses older desktops' deletes of rates
  made elsewhere.
- Deletions made here are kept in `custom-rates-deleted.json` until the
  cloud confirms them. `CustomRate` carries `CloudId` (website slug ids),
  `CloudUnit`, `SectionKey`, `SectionLabel`, `CloudUpdatedAt` and
  `SyncedSignature`.

## v3.0.0

RateGen in Richard Enoch's ADLM suite design, with services split into four
disciplines and a carbon figure for every priced rate. Two pricing fixes move
some rates: blockwork and render (mortar) go up, and some cloud services rates
come down (an uplift was being added twice). Both are explained below.

### A new look, the same as every ADLM product

- The rail on the left: the Library, every trade under Item of Works, a
  Services group, Carbon, and your Saved Rates with a count. Below 1100 px wide
  it folds to icons by itself; Ctrl+B folds or unfolds it.
- Every trade screen shares one layout: find, sort (bill order, most expensive,
  A to Z), and a table you open with a click or Enter to see the rate's
  build-up. Quantities in a build-up are edited in place; Enter keeps them.
- The Library (materials and labour) uses the same table, find and sort.
- Messages and questions are ADLM dialogs, not Windows message boxes.
- Loading shows the step it is on ("Preparing your library", "Loading the rate
  build-ups") and moves when a step finishes.
- The splash and sign-in follow the suite design.
- The window fits the screen at any size and scaling, down to 860 px wide.
- "To CM App" is now **Open ADLM Cloud**, which opens RateGen in your ADLM
  account.
- RateGen's own icon, at every size, on the taskbar and the desktop.

### Services: Mechanical, Electrical, Plumbing and Fire

Services rates are split into four disciplines, each with its own screen. The
ADLM cloud's services rates are filed under their discipline and priced once:
the 35% uplift some carried on top of their price is no longer added again.

### Carbon

Carbon & Others shows the upfront embodied carbon (RICS whole life carbon,
modules A1-A5) of every priced rate, worked out from the same materials and
quantities as its price. Open a rate to see each line's carbon, its factor and
the factor's published source; a rate that reuses another (mortar in a wall,
the mixer in concrete) takes that rate's carbon.

- A rate with cement shows a range. The low end takes the Nigerian producers'
  own cement figure (Dangote 2024, 570 kg CO2/t; Lafarge Africa 2024, 537; the
  kiln only, so a floor), the high end the full cradle-to-gate 0.83 kgCO2e/kg.
- Reinforcement uses scrap-based electric-furnace steel (CARES EPD 0060,
  0.821 kgCO2e/kg), the route Nigeria's own rebar mills use.
- Coverage says how much of a rate's cost the figure accounts for; * marks a
  rate that uses an assumed mass. Labour and plant hire carry no material carbon.
- Services, windows and doors, asbestos sheets and ceilings have no carbon yet:
  there is no published factor or weight to work from.

### Fixed: mortar was a quarter of what a wall actually buys

A 225 mm wall carried 0.054 bags of cement per m2 against about 0.20 in
practice. Mortar mixes now allow for dry materials shrinking when mixed, and the
mortar bed matches the Lagos build-up rate. A 225 wall now buys 0.195 bags per
m2 and 12 mm render 0.146, so blockwork and render rates go up.

### Also fixed

- Refreshing one trade's rates no longer wipes the rates cached for the others.
- A cloud services rate is priced from the line it names, not its description.

## v2.9.2

### Build with AI now expects you to ask for a rate

The prompt box only accepts a request that asks for a rate build-up in so many
words — it has to contain both "rate" and "build". "Build a rate for 225mm
hollow sandcrete blockwork in cement-sand mortar (1:6)" is the shape it wants.
"Building", "builds", "rates" and "rebuild" all count.

Every request spends part of your AI allowance, and the service will answer
whatever it is given, so a question that is not a work item costs you a request
and returns something RateGen cannot turn into a rate. The panel heading now
carries an example, so the expected phrasing is visible before you type rather
than after a request is refused.

Note the trade this makes: a bare work item — "225mm blockwork in cement
mortar" — is now turned away even though it is a perfectly good description.
Put "Build a rate for" in front of it.

### Technical detail

- `CustomRateEntryViewModel.MentionsRateAndBuild` is a case-insensitive
  substring check on the two stems, run before the request is sent, so a refused
  prompt costs nothing.
- It is a keyword check and not comprehension, which cuts both ways: "build a
  rate for a birthday cake" satisfies it. Both that and the rejected bare work
  item are pinned in `AiPromptGuardTests` so the limits of the guard are
  recorded rather than rediscovered.

### Fixed: the same material could be saved into your library twice

Library lookups matched on the exact string. So when the AI drafted a component
as "Cement (Portland 42.5 R)" and your library held "Cement (Portland 42.5R)",
the lookup missed — the line kept the AI's price, and saving the rate wrote it
in as a **new** row. Build the same work item again next week, get another
spelling, get a third cement. The same class of near-miss cost two material
lookups in v2.8.0, where one missing space priced a component at zero.

Names are now compared with case, spacing and stylistic punctuation set aside,
so a component that differs only in how it was written finds the row you already
have. An exact name still wins over a near match.

What is deliberately *not* collapsed: sizes and mixes. 1.2mm and 12mm roofing
sheet stay separate items, as do mortar (1:6) and (1:3), and "Cement" and
"Coloured Cement". Merging those would misprice a rate rather than tidy a
library, so the decimal point, the slash and the colon are all kept.

**And the AI is now told what you have.** Each build request carries your own
library's names — your additions first — and the service is instructed to name a
component exactly as you hold it. So the duplicate is prevented at both ends:
the model reuses your name, and if it does not, the tolerant match catches it
anyway. Names and units only; no prices leave your machine.

### Already working, for the record

Materials and labour drafted by AI are folded into your library when you save
the rate, under their real names — the `[AI]` and `(plant)` tags are stripped
first, so the library does not fill with tagged duplicates. Entries you already
have are never overwritten, and only priced lines are taken. Harvested items are
filed under the "Custom Rate" category.

They are then reusable in both directions: they appear in the material and
labour pickers, and on a later AI build any component matching one of them is
priced from *your* library, with the AI's own figure discarded.

Until this release the AI service could not see any of that: it grounds on the
master price library, so it had no way to propose an item you had added
yourself. Sending your library with the request, above, is what closes that.

---

## v2.9.1

### Fixed: "Build with AI" was missing after the update

The AI panel on the Custom Rate form did not appear for anyone who installed
the update. Nothing was wrong with the feature itself — it was hidden.

The panel is shown only when the app knows where the ADLM AI Service is, and
that address was read from an `ADLM_AI_URL` environment variable with no
fallback. Nothing sets that variable on a user's machine: the installer does
not write it and the app does not create it. So the address came back empty on
every install, the AI client was never created, and the section was hidden —
the one machine where it appeared was the development machine, which had the
variable set by hand. The feature shipped invisible.

The app now falls back to the deployed service address, so a plain install has
the AI panel with nothing to configure. Update to 2.9.1 and it is there; no
environment variable, registry entry or reinstall is needed.

This is a patch on top of 2.9.0 and carries nothing else. It is numbered 2.9.1
rather than folded into 2.9.0 because InstallerHub decides whether to offer an
update by comparing the published version string: republishing under 2.9.0
would have left everyone already on 2.9.0 — which is everyone — never offered
the fix.

Using it still requires being signed in with an active subscription — the
service checks your licence on every request and says so plainly if it
declines.

### Technical detail

- `AppEnvironment.AiServiceUrl` resolves in the same order as `ApiBaseUrl`: the
  product override (`ADLM_RATEGEN_AI_URL`), then the fleet-wide variable
  (`ADLM_AI_URL`), then the new `DefaultAiServiceUrl` constant. Trailing
  slashes are trimmed, since the SDK appends `/api/ai/...` verbatim.
- Either variable set to `off` (or `none`/`disabled`/`false`/`0`) disables AI
  deliberately and hides the panel. That is now the only way it disappears; a
  blank value is ignored rather than read as "off", so an installer writing an
  empty string cannot switch the feature off by accident.
- Deliberately *not* fixed in the installer. Writing the address into
  `HKCU\Environment` would recreate the stale-variable problem v2.6.0 had to
  clean up after with the retired API host: a value baked in at install time
  outlives the machine it was right for. The default lives in the app, where a
  release can change it.
- 11 new test cases in `Tests/ADLMRateGen.Tests` covering the empty environment, both
  overrides, the off switch and blank values. `InternalsVisibleTo` added so the
  test project can see `AppEnvironment`. Suite is 35 cases, all passing.
- `scripts/push-installer-update.mjs` publishes a build to InstallerHub in one
  command: it hashes the package, reads the deployment record, uploads to R2 and
  writes `packageUri`, `version` and `sha256` back together. It reads the record
  before writing because a `PUT` to `/admin/deployments/:productKey` replaces
  rather than patches — sending only the changed fields silently wipes the
  install operations — and it refuses a push that would change the package kind,
  because the operations and the package have to agree. RateGen's deployment is
  a ZIP containing `app/`, which InstallerHub extracts itself; the Inno installer
  is the manual-download artifact and is not what this channel consumes.

---

## v2.9.0

### The library now says what a rate is meant to produce

A day rate is a proxy for an output, but the library only ever showed the
money. Deciding whether ₦250,000 a day is right for a D6 meant knowing from
memory that a D6 shifts 150–250 m³ an hour.

The labour edit popup now carries a reference panel: what the item is, what it
produces, what that assumes, and what it burns or who staffs it. All 84 labour
and plant rates have one. Every output is given as a range with its basis,
because published sources disagree by up to a factor of two on the same
machine, and a single number would imply a precision that does not exist.

It is read-only and feeds no calculation. It is there to sanity-check a rate,
not to drive one.

Rates you add yourself can now carry a note of your own — your plant, on your
terms. Your note wins over the bundled entry, since you know your own machine
better than a published table does, and a rate you added has no bundled entry
at all. Notes travel with the rate to the website.

The catalogue also moves from three categories to 24, and sorts by size rather
than alphabetically, so 12mm comes before 100mm.

### Published prices can be accepted one row at a time

The price notice was a one-line bar with two buttons that applied to everything
at once. It named the affected rates but never showed a figure, so the only way
to judge an offer was to accept it and go and look. It could not be dismissed
either — it sat above the library until answered.

It is now a card, one row per rate: a tick box, the unit, the price you typed,
the price since published, and the percentage between them, coloured by
direction. Both buttons act on ticked rows only, so you can take the new cement
price and keep your own on diesel. Whatever you leave unticked stays pending
and the card stays up with the remainder. The header has a select-all, the
footer counts what you have selected.

Closing it hides it without deciding. The rows stay pending, and a pinned card
in the notifications popup brings the review back.

### Sync moved to the header, and now says why a step failed

Sync sits with the other app-wide actions in the header instead of in the
sidebar, with its status on the button's tooltip.

It also explains itself. A failed step used to read "Master prices: FAIL" with
no reason — the error handling replaced the exception with that literal string
and threw away a message that already carried the HTTP status and the server's
own text. And an empty response was indistinguishable from a crash, so "the
server had nothing for you" arrived looking like a fault. Both now say what
actually happened.

**Fixed: sync could die on a duplicated library row.** Merging your rows onto
the master list appended a twin instead of folding your price onto the matching
row, so any item you had priced could end up in the library twice. Six pairs had
built up this way, and the duplicate then crashed the sync outright. Existing
duplicates clear themselves on the next sync.

### The rate tables have room

A paying user reported seeing only one line of a rate table at a time. The
welcome banner takes 260px off the top, and every rate view carried a negative
bottom margin that pushed its card past the workspace, clipping the last row in
half.

A chevron in the header now collapses the banner and hands its height to the
table, and the negative margins are gone. The choice persists between sessions,
and is kept out of the file that is cleared on sign-out, so signing out does not
reset it.

### Screenshots carry their source

A captured price list is now marked with its origin. Earlier in this release the
mark was drawn over the window itself, which worked but sat over the figures all
day; it now goes onto the captured image instead, so the app is clean to work in.
PrintScreen, Alt+PrintScreen and Win+Shift+S all route through the clipboard,
which is where the mark is applied.

Note the limit honestly: a photograph of a monitor carries no mark, and no
software control reaches that route.

### Fixed: names, locations and dark mode

- **Roller and grader names corrected.** "Vibratory whelled roller" was not a
  word, and a pneumatic roller runs on tyres. "Grader (Cat 1406)" is now
  "Grader (Cat 140G)" — 1406 is not a Caterpillar model, and the G was being
  read as a 6. A rate name is a lookup key, so the old spellings are aliased:
  an installation that has not yet updated still prices these rates instead of
  silently costing them at zero.
- **The pricing-location note keeps up with the sync.** The library could read
  "Lagos: priced from south west rates" for an account whose profile says Imo.
  Nothing was mispriced — only the sentence was stale, because it was re-read
  on the way into the library and never again.
- **Dark mode.** The Material Name and Labour Item columns went blank, the
  price review was unreadable, and the save button and sign-out took the wrong
  colour. These set a brush that resolves once at load and never follows a
  theme change.

### Technical detail

- `PriceConflictRow` wraps the price-conflict DTO rather than extending it: that
  type is written to disk and compared by value, and a tick box has no business
  in a persisted record.
- The banner state lives in `ui.preferences.json` under `AppPaths.UserDataDir`,
  deliberately apart from `AppConfig`, which is rewritten on sign-in and cleared
  on sign-out.
- The duplicate-row crash was `FindEdits` emitting two edits sharing a `RowKey`
  while every consumer builds a dictionary off that key. The merge now folds a
  matching user row onto its master row, `FindEdits` dedupes the way its incoming
  side already did, and the four `RowKey` dictionary builds group first.
- `GetJsonRawAsync` turns 404 and 204 into `"[]"`, so an empty response reached
  `SyncAsync` as a JSON array and every `TryGetProperty` threw — which also made
  the `Is404` "SKIPPED" branch unreachable. The root kind is checked now.
- `csproj` and `Installer.iss` move together, so a package cannot install 2.9.0
  binaries while announcing 2.8.1.

---

## v2.8.1

### Fixed: the stale API host is now removed, not just ignored

Older RateGen installers wrote the retired `adlmweb.onrender.com` host into your
machine's environment. v2.6.0 taught the app to step over that value, which is
what has kept affected machines working — but ignoring it is permanent: the
variable stays on the machine forever, and the guard has to live forever to keep
stepping over it.

It is now deleted on startup, so the cause goes away rather than being routed
around. The app deletes rather than corrects, because the right value is not the
app's to decide: with the variable gone, the address falls through to the current
default.

### Open any component in the library, from any section

Clicking a component in a rate breakdown opens it in the library, ready to edit
its price. This worked in Carbon and Others only; it now works in all ten
sections, through one shared implementation rather than ten copies.

That matters more than the feature does. There are ten near-identical breakdown
line types, and threading this through each of them by hand would have been ten
chances to repeat a mistake the first version already made: a binding that
resolves against the wrong object gives a link that looks right and quietly does
nothing.

### Pricing location comes from your website profile, and only from there

The state picker is gone from the library. Pricing location is set once, on your
ADLM profile on the website, and arrives on the next sign-in.

Two places could otherwise disagree about where you price from. Worse, the app
was still sending its own stored state to the server, and the server prefers the
state it is given — so changing your profile on the website would appear to do
nothing, silently and permanently. The setting shipped defaulted to "Lagos", so
this would have applied to every user who never touched it: exactly the people
most likely to rely on the website picker.

The sync now asks for "my prices" without naming a state at all, and reads back
the state the server actually priced from.

### The library Undo has its own button

The header's existing Undo arrow belongs to "reset all rate edits", which throws
away every section's quantity edits app-wide. The library restore now has its own
icon that reads as "go back to an earlier point", so two destructive actions no
longer look like one. It appears only on the Library screen.

### Technical detail

- Released as 2.8.1 rather than folded into 2.8.0 because 2.8.0 was already live
  on the Hub. Two different binaries answering to the same version make it
  impossible to tell from a user's machine which one they are running.
- `Helpers/LibraryLink` holds the breakdown-link command and each line binds it
  against its own data context — no `RelativeSource`, no `Tag`, nothing
  per-section. The Carbon-specific command, event and Tag hack are removed.

---

## v2.8.0

### Fixed: signing in destroyed prices you had edited

The cloud sync wrote back "master rows, plus the rows you added yourself". A
price you had edited **on a master row** was in neither list, so it was destroyed
on every sign-in — silently, with no way back. This was happening to everyone,
not only when changing location.

Your edits are now carried forward. The app records what the server last sent,
which is the only way to tell a price you typed from a price the server sent, and
that difference is what it preserves.

Where the server has genuinely moved a price on a row you edited, your figure
still wins and the disagreement is parked for you to accept or ignore from the
library. Rows the server has not moved are kept with no prompt at all — asking on
every launch would only train you to click through the dialog.

**Undo, for the whole library.** Both libraries are snapshotted before any sync,
location change or price acceptance, and the last 20 sit behind an Undo button. A
restore snapshots the current state first, so the undo is itself undoable.

### Fixed: Carbon and Others rendered empty

The server had its 26 items, enabled and resolving correctly, and the section
showed nothing.

Each of the ten sections refreshed the shared item store with only its own
section's items, last writer wins — and a section with no cloud items of its own
wrote an empty file, wiping everything. All 26 cloud items belong to Carbon, so
any other section syncing emptied the store. The other nine sections are
hardcoded and merely append cloud items, so losing them changed nothing on
screen; Carbon and Others is the only entirely cloud-driven section, so it was
the only one that went blank.

### Rates that had been costing nothing

An audit of all 326 material and 461 labour lookups across every engine found
four names that do not exist in the catalogue. A missing name prices the
component at zero and reports it nowhere, so the rate looks fine.

- Two were typos with an exact row sitting in the catalogue: a formwork plywood
  size missing a space, used by Blockwork and Concretework, and a window size
  with its dimensions transposed.
- **Door ironmongery** had no row at all — the code carried a placeholder
  comment saying so. There is now a Door Ironmongery category: the set, brass
  butt hinges and a door stop, with the lock left as the catalogue's existing
  Mortise Lock.

Both typos predate the master reseed, so nothing here was caused by that change.

### Structural steelwork and roof carpentry

The steel section had three items and all three were surface preparation — the
product could clean steel but could not price any. Nine build-ups added:
universal beam and column, angle and channel each per tonne and per kg, plate
for gussets and base plates, and a 6mm fillet weld per metre. Both units are
offered because steelwork is taken off both ways. Each is supply, fabricate and
erect, the way a Nigerian bill measures it.

The roof section priced the covering and nothing holding it up: all 16 items were
sheeting, cappings, felt and zinc, and several of their own descriptions said
"laid on purlins (measured separately)" while nothing measured them. Six
build-ups added — wall plate, rafters, purlins and the rest — with the timber
grades they need restored.

### Price by state

The library prices against a state, chosen beside the tabs since it governs
materials and labour together, with the zone the price actually came from shown
underneath. All 36 states and the FCT.

The setting was always a state; it was only called Zone. It shipped defaulted to
"Lagos", which is not one of the six geopolitical zone keys, so the server
rejected it and quietly fell back to the account zone — meaning the value you
could see was never the value that priced your work. (This picker is removed
again in 2.8.1, in favour of the website profile.)

### Technical detail

- `SyncBaseline` records the server's last-sent state; `PriceConflicts` holds
  disagreements; `LibraryArchive` keeps the snapshots. Materials and labour save
  back to back, so the archive coalesces to one snapshot per sync rather than two,
  half of them useless.
- The compute-store refresh now always persists the full catalogue, with the
  section key deciding only what a reader filters to — the readers previously
  iterated the whole store with no filter of their own, which is why the bug
  looked like working behaviour.

---

## v2.7.0

### The first full price refresh since launch

All 568 catalogue rows recalibrated against three independently priced 2026
bills, with the primary commodities then set from observed market prices:
cement ₦4,300 → ₦11,500/bag, HT rebar ₦350,000 → ₦1,700,000/tonne, labourer
₦1,800 → ₦5,400/day.

Aggregates were corrected too — sharp sand ₦4,500 → ₦6,500/m³, granite 15–25mm
₦17,400 → ₦9,500/m³. The granite figure had drifted to nearly double the
observed price because it was the dominant free lever in the concrete fit and had
absorbed the residual error of every other term. Correcting it moves reinforced
concrete from +16.6% to +9.7% against the bills it was calibrated on.

15–25mm granite and hardcore filling also change unit from tonne to m³, matching
how the engine has always consumed them.

**Your edited prices are kept.** The refresh three-way merges against a frozen
baseline: untouched rows refresh, rows you edited are preserved, missing rows are
re-added.

### An MEP section, and an engine to price it

67 material rows across 11 new categories, every one from a verified priced line
in the three 2026 bills, plus 37 build-ups across 9 sections.

These are **installed** rates — the bills price MEP as "supply, fix, connect &
commission" — so every category name carries "(supply & install)" and the engine
adds no labour line of its own. Adding one would double-count the fixing.

Pipes are not included: no pipe price exists anywhere in the three bills, and
none were invented from an index. One source figure is flagged rather than
silently replaced — a 12W LED ceiling fitting billed at 2.6× the same book's
600×600 LED panel, which is implausible for a 12W fitting. It ships as billed.

### Fixed: three costing defects that shipped with the old prices

- Concrete mixing labour was charged at a full day rate against an hourly
  quantity.
- Fuel was priced off the labourer rate rather than the Diesel material.
- The tonne/m³ unit mismatch on aggregates, above.

The first two had to be fixed in the same release as the new prices. Shipping
tripled wages against the mixing bug would have diverged every concrete rate in
the product.

### Technical detail

- Delivery runs through the existing `DataMigrator` as version 4 rather than a
  parallel mechanism. Its `MergeDefaults` was add-only, so it had never refreshed
  the price of a row it already knew about — every existing install would have
  kept its 2022 prices indefinitely.
- The working catalogue moved to `%AppData%\ADLMRateGen` via `AppPaths`, not
  beside the executable, so the merge writes somewhere it is allowed to.
- Minor rather than patch: every computed rate in the product changes.

---

## v2.6.2

### Fixed: saving a rate could not add to your library, and risked losing rows

Your material and labour libraries can hold more than one entry under the same
name — the same material at a different unit, or in a different category. The
shipped material list does this 23 times.

When a rate was saved, the step that adds new materials and labour to your
library rebuilt the whole list keyed on the name. With duplicates present that
step failed outright, and because the failure was caught and ignored, saving
appeared to work while nothing was ever added to the library. Had it not failed,
it would have written the list back with only one row per name and discarded the
rest.

The merge now keeps every row. Existing entries are updated in place, so
duplicates and the order of your library are both preserved, and genuinely new
items are added at the end. When an update matches a name held by several rows,
all of them take the new price.

If your library lost rows before this release, this does not bring them back —
restore from a backup if you have one.

---

### Technical detail

- `MaterialLibraryService.AddOrUpdateMaterials` and
  `LabourLibraryService.AddOrUpdateLabours` used
  `ToDictionary(m => m.MaterialName)`, which throws `ArgumentException` on a
  duplicate key; `CustomRateEntryViewModel.SaveCustomRate` wraps `Harvest` in a
  try/catch, so the throw was swallowed and the harvest silently no-opped.
  Reassigning the backing list from `dict.Values` would also have dropped every
  duplicate row.
- Both now group by name and mutate the matched rows in place, appending only
  unseen names, so row count and ordering are stable.
- 5 new tests in `Tests/ADLMRateGen.Tests` (24 total), verified as a negative
  control: against the previous implementation they fail 4/24.

---

## v2.6.1

### Fixed: AI-built rates saved with every price at 0.00

Building a rate with AI filled the form correctly, but saving it wrote every
price as 0.00 — and reopening the rate showed zeros. The quantities, units and
names were all fine; only the money was gone.

Saving a rate adds any new materials and labour to your library and then reloads
it. That reload was re-pricing every line on the form, and because an AI line is
labelled "Cement (Portland 42.5R) [AI]" it never matched the library entry named
"Cement (Portland 42.5R)" — so it was set to zero. This happened in the moment
between you pressing Save and the file being written.

Lines now match their library entry regardless of the label, and a reload can no
longer clear a price that is already there. Your existing zeroed rates cannot be
recovered — rebuild them and they will save correctly.

### Fixed: Overhead and Profit are no longer wiped by a bad keystroke

Clearing the Overhead or Profit box left it looking empty while the old value
was still being used in the total. Both boxes now keep exactly what you type.

### ADLM AI: clearer feedback while it works, and when it doubts itself

- The AI panel glows while a build-up is being prepared, the button reads
  "Building…", and both it and the prompt box grey out. They were already
  disabled during a request, but looked live.
- A build-up that comes back with no prices at all is now refused rather than
  presented as ready to save.
- When ADLM's checks flag a build-up — a labour line priced per day that was
  never pro-rated to one unit, a rate wildly out against comparable library
  rates, a single line costing more than a whole comparable rate — the reasons
  are listed in an amber panel under the AI box. The draft is still shown, but
  you are told what to correct before saving.

---

### Technical detail

- `RateEntryItem.ResolveUnitPrice` matched on the raw description and cleared the
  price on every miss. It is wired to the static
  `MaterialLibraryService.LibraryChanged`, which `SaveCustomRate` raises via
  Harvest + `RefreshLookups`, so every unmatched line was zeroed mid-save. It now
  matches on `RateLineLibrary.CleanName` and only clears when the user actively
  picks a different item (`clearWhenUnknown`).
- `AiRateService` derives a unit price from `totalNgn / quantity` when
  `unitPriceNgn` is absent, and `IsUnpriced` gates an all-zero build-up.
- `AiRateResult.Warnings` carries the service's check failures through to
  `CustomRateEntryViewModel.AiWarnings`.
- `OverheadPercentText` / `ProfitPercentText` hold the raw text; a decimal
  binding blanked the field on a partial entry. `FInputOHP` also had a fixed
  `Height` while `FInput`'s template puts `Padding` on the Border, leaving ~23px
  of content slot and clipping the value out of view.
- Added `Tests/ADLMRateGen.Tests` — 19 tests over the pricing and harvest rules,
  hermetic via in-memory data sources. Verified as a negative control: against
  the previous `RateEntryItem` they fail 5/19.

**Server-side, already deployed:** the AI service now enforces pro-rating and
sanity checks in code rather than only in the prompt, and escalates to the
stronger model when a build-up fails them.

---

## v2.6.0

### Your custom rates now build up your price library

Every material and labour line you price on a custom rate is added to your library when
you save it. Type a material once and it is in the dropdown — with its rate and unit —
for every rate you build afterwards.

Prices you already have are never changed. If the library already knows an item, saving
a rate that uses it at a different figure leaves your library price alone; only items
the library has never seen are added. New entries appear under the category
**Custom Rate**. The save confirmation tells you how many were added.

### Build with AI now uses your prices

When the AI drafts a rate build-up, any component that exists in your library is priced
**from your library**, at your rate and your unit — the AI's own figure is discarded.
Only components your library has never heard of keep the AI's estimate, and those stay
tagged `[AI]` so you can see at a glance which lines still need your review.

Previously the AI's price overrode your library on every line.

### Press Enter to sign in

Enter from either the email or the password field signs you in. Enter on
"Forgot password?" and "Create account" still opens those pages.

### Overhead and Profit are readable again

The Overhead and Profit boxes were rendering their value clipped out of view, so they
looked empty. Both now show their percentage clearly, with the cash value each one adds
shown underneath, and the totals block gained **Overhead**, **Profit** and a
**Grand Total** line — the grand total was being calculated but never displayed.

---

### Technical detail

- Added `Services/RateLineLibrary.cs` — `CleanName()` strips the `[AI]` / `(plant)`
  provenance tags from a line description; `Harvest()` upserts unseen priced lines into
  `MaterialLibraryService` / `LabourLibraryService`. Existing entries are skipped, not
  overwritten. Called from `CustomRateEntryViewModel.SaveCustomRate()` inside a
  try/catch so a library write failure cannot make a successful rate save look failed.
- `Services/AiRateService.MapToCustomRate()` now resolves each component against the
  local library first and stores matches under the library's canonical name, letting
  `RateEntryItem.ResolveUnitPrice()` supply price and unit. It previously assigned the
  server price last specifically to override that lookup.
- `CustomRateEntryViewModel` exposes `OverheadPercentText` / `ProfitPercentText`
  (string) plus `OverheadAmount` / `ProfitAmount`. The percent boxes bound straight to
  `decimal`, and a partial entry ("", "1.") failed to convert and blanked the field.
- Fixed `FInputOHP` in `CustomRateEntryView.xaml`: the style set a fixed `Height` while
  `FInput`'s template applies `Padding` to the Border around a vertically-centred
  `PART_ContentHost`, leaving ~23px of content slot and clipping the text out of view.
  Now `MinHeight` with zero vertical padding.
- Fixed `CustomRateEntryViewModel.LoadRate()`: `RateType` was assigned last in the
  object initializer, re-triggering the library lookup and zeroing the price of any
  saved labour line the library does not know (all `[AI]` lines).
- Added `KeyDown` handling on the sign-in fields rather than making Log in the window's
  default button, so Enter on the footer links keeps its own behaviour.

---

## v2.5.0

### Fixed: repeated "already bound to another device" sign-in failures

Some customers were blocked at sign-in with **"This subscription is already bound to
another device (DEVICE_MISMATCH)"** every time they logged in — on the same computer,
after restarting, and after updating, without ever having signed in elsewhere.

The cause was the way Rate Gen identified your computer. It used the network adapter
that happened to be fastest and active at the moment you signed in. That adapter
changes when you connect a docking station or USB network adapter, when a VPN starts
or stops, or when the machine switches between Wi-Fi and a cable — and on laptops with
both Wi-Fi and Ethernet it could change on its own as Wi-Fi signal strength varied.
Each change made the same computer look like a brand-new device to the licence server.

Rate Gen now identifies your computer from hardware that does not change between
sessions — the processor, BIOS and motherboard identifiers — matching the method
already used by ADLM Installer Hub, PlanSwift, QUIV and the Revit plugins.

**You do not need to do anything.** Your existing licence is recognised and moved
across to the new method automatically the first time you sign in after updating.
Docking stations, VPNs, USB adapters and Wi-Fi/Ethernet switching no longer affect
sign-in.

If you were locked out before this update, sign in once after installing v2.5.0 and
access is restored.

---

### Technical detail

- Added `Helpers/HardwareFingerprint.cs` — `SHA-256(CPU ProcessorId | BIOS Serial |
  Baseboard Serial)`, byte-identical to the implementations in ADLM Installer Hub,
  PlanSwift, QUIV, RevitPluginArch2026 and the Revit MEP plugin, with a
  MachineGuid-based fallback when WMI is unavailable.
- `Services/AuthProvider.cs` and `ViewModel/SignInViewModel.cs` now use it. They
  previously called `ADLM.Auth.DeviceFingerprint`, the legacy MAC-based value.
- `ADLM.Auth/DeviceFingerprint.cs` is retained and marked legacy. Its algorithm is
  frozen; it is only used to supply the old value for binding migration.
- Login now sends `x-adlm-fp-version: 2` plus `fp_version` and
  `device_fingerprint_legacy` in the request body, so the server can match an existing
  v1 binding and re-bind it to the stable value instead of rejecting the sign-in.
- Offline licence validation accepts either the v2 or the legacy fingerprint, so cached
  licence tokens issued before this release do not force users back online.
- Added the `System.Management` package reference (WMI).

**Server dependency:** the migration path requires the licence server to read the
legacy fingerprint and re-bind. Confirm the field names match the backend before
release, otherwise upgrading users will bind as new devices.

**Not included in this release:** the self-service device management view, the
"release this device" action and the redesigned mismatch dialog are still outstanding.

---

## v2.4.0

Editable rate build-up quantities with per-user persistence and cross-app cloud sync to
QUIV / HERON via `/rategen-v2/library/user-rates`.
