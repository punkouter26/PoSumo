# UI Toolkit in PoSumo

Every screen in this game is UI Toolkit, built from C# at runtime. There is no
UGUI Canvas anywhere in the project, no `.uxml`, and no `.uss`. `Systems_UiKit`
is the design-token layer and the only place a font size, gap, radius or colour
should ever be chosen; `Systems_HudRoot` is the single `UIDocument` the match
screen draws through.

```
Systems_UiKit              tokens + builders (type scale, space scale, palette,
                           Card / Triplet / Divider, the button family, motion)
Systems_HudRoot            the match screen's one UIDocument and its layout bands
Systems_SafeArea           notch / gesture-bar insets, one watcher per screen
Systems_GameMatchManager   top bar, callouts, result card, pause card
Systems_FightHud           live strip (dock) + between-rounds detail card (stage)
Systems_TournamentBracket  the bracket screen, its own UIDocument
Systems_CareerScreen       the banzuke overlay, a LAYER inside the bracket's document
```

`Systems_CareerScreen` is the worked example of the layering rule below: it is a
full-screen modal that does **not** add a second `UIDocument`. The bracket's root
is unpadded and hosts three layers — the bracket screen, the drag ghost, and the
career overlay — and `Systems_SafeArea.Attach` is given the bracket's content
layer and the overlay's modal layer as two targets, so the scrim between them
stays full-bleed. That is what `Attach`'s `params` signature is for.

## Things that will bite you

**Inline styles beat every USS rule.** `element.style.X` resolves above any
stylesheet selector, including the default runtime theme's `:hover` and
`:active`. Any builder that writes `backgroundColor` inline has to put the
interaction feedback back by hand — `Systems_UiKit.AddPressFeedback` is where
that happens, and it is called from `StyleButton`, so every control built
through the kit gets it. A button built by hand will be visually dead on press.

**Absolute children resolve against the parent's *padding* box.** This is why
the safe-area inset is applied to the content layer and the modal layer but
never to the scrim between them: a scrim under the inset stops at the notch and
leaves an undimmed strip at the top and bottom of the screen.

**The panel scales on WIDTH.** `GamePanelSettings` is `MatchWidthOrHeight` with
match `0` against a 720x1280 reference, so a point is a constant fraction of the
screen's *width* and the panel's height in points varies from ~960 (4:3 tablet,
portrait) to ~1600+ (20:9 phone). Band heights authored in points therefore take
a different share of the screen on every device. `Systems_HudRoot` handles that
with proportional guards — `Stage` has a 45% minimum and `Dock` a 28% maximum.

Do **not** "fix" it by moving the PanelSettings to a balanced match (`0.5`)
instead. That makes the panel *narrower* than 720pt on tall phones, and the
bracket's chip row is sized against exactly 720pt: four `SLOT_SIZE` chips plus
margins come to 688pt against 696pt of usable width. A balanced match overflows
it, which is a bug that has already been fixed once.

## Developer action items

These need Unity Editor work that cannot be done from a script, and until they
are done the game is running on defaults.

### 1. Font asset (required — this is the biggest single visual win left)

The project has no `.ttf`/`.otf` anywhere and `GamePanelSettings.textSettings`
is empty, so every screen renders in Unity's default UI font. To fix:

1. Drop a condensed bold display face (for `FONT_TITLE` / `FONT_HERO` /
   `FONT_MEGA` — the score digits, the round banner, the countdown) and a clean
   UI face (everything else) into `Assets/UI Toolkit/Fonts/`.
2. For each: select it, set **Character Set** to include the glyphs listed
   below, and create a **Font Asset** (`Assets/Create/Text/Font Asset`).
3. `Assets/Create/UI Toolkit/Panel Text Settings`, save as
   `Assets/UI Toolkit/PoSumoTextSettings.asset`.
4. Put the UI face in its default font asset and the display face in its
   **Fallback Font Assets** list.
5. Assign it to `textSettings` on `Assets/Settings/GamePanelSettings.asset`.
6. Set the display face per element via `label.style.unityFontDefinition` in
   `Systems_UiKit` — add a `Display` token next to the type scale rather than
   assigning fonts at call sites.

**Glyph coverage to verify before shipping**, because these are all currently
in use and a font that lacks them renders boxes:

| Glyph | Where |
|---|---|
| `·` U+00B7 | round footer, SHOVES · BEST, bracket status line, career button, banzuke rung holders |
| `—` U+2014 | empty stat values, empty bracket chips, CHAMPION line, empty banzuke rungs |
| `→` U+2192 | bracket round arrows |
| `★` U+2605 | titles column on the career screen's fighter cards |
| `▸` U+25B8 | career button (opens the banzuke overlay) |

`▴` / `▾` U+25B4/U+25BE were retired with the collapsed career table the career
screen replaced. Nothing draws them any more — do not re-add them to a font's
character set on this table's account.

The pause button is drawn as two `VisualElement` bars rather than a glyph
precisely so it has no font dependency. Keep it that way.

### 2. USS + UI Builder (optional — an authoring upgrade, not a fix)

`Systems_UiKit`'s header explains why the tokens are C# and not a stylesheet:
a stylesheet resolved by `Resources.Load` can silently fail and leave the game
unstyled. That reasoning holds for a *path*-resolved sheet. It does not hold for
a **GUID reference**:

```csharp
[SerializeField] private StyleSheet _sheet;   // cannot silently fail to resolve
...
if (_sheet != null) root.styleSheets.Add(_sheet);
```

If you want UI Builder, USS class variants and native `transition-*` properties,
that is the safe shape: a serialized reference with the code tokens as the
fallback. **Do it as one move or not at all** — a `.uss` that duplicates the
token values while the C# copy stays authoritative is a second source of truth,
and this repo has been bitten by exactly that failure mode twice already (the
music stems and `VoiceGains.asset` both went stale against fixed generators).

### 3. Runtime data binding (optional — a code-shape upgrade)

`Systems_FightHud` currently dirty-checks every label before writing it, which
is what keeps the per-frame path allocation-free. Unity 6's `[CreateProperty]` +
`dataSource` / `SetBinding` with `BindingUpdateTrigger.OnSourceChanged` would
push that bookkeeping into the framework and delete `UpdateLiveStrip`'s manual
caches. It needs `INotifyBindablePropertyChanged` on the source, so it is a real
refactor rather than a drop-in.
