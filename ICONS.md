# Drover icons

Drover ships four state icons. The same silhouette across all states (offset
rounded squares, outline back + solid front) — only the fill colour changes.
This keeps the silhouette instantly recognisable while the colour shift
communicates state at a glance.

## States

| State       | Back outline | Front fill | Meaning                                |
| ----------- | ------------ | ---------- | -------------------------------------- |
| `idle`      | `#A78BFA`    | `#7C3AED`  | App running, sessions idle (default)   |
| `working`   | `#A78BFA`    | `#0EA371`  | At least one session is processing     |
| `attention` | `#FFC940`    | `#FFC940`  | Claude is awaiting your input          |
| `inactive`  | `#444444`    | `#666666`  | App running, no sessions open          |

All four use the same near-black backing tile (`#1a1a1a`) so the icon owns its
contrast against any tray background — Windows 11 dark, light, custom themes.
No transparent variant is shipped intentionally.

The Attention state uses amber for **both** squares (not just the front)
because at 16px the colour difference between "purple-and-amber" and
"all-amber" reads instantly. Front-only colour swaps at that size are too
subtle to be useful.

## File layout

```
drover.svg                  master 256px SVG, idle state — for app icon, web, README
drover.ico                  default ICO, == drover-idle.ico

ico/
  drover-idle.ico           multi-resolution: 16/24/32/48/64/128/256
  drover-working.ico
  drover-attention.ico
  drover-inactive.ico

png/
  drover-{state}-{size}.png each state at each size (28 PNGs)

svg/
  drover-{state}-{size}.svg per-size SVG masters with hand-tuned strokes
```

## Why per-size SVG masters

The outline stroke weight is hand-tuned per size, not scaled from one master.
At 16px a stroke under 1px disappears under WPF's pixel snapping. Each size's
stroke is the minimum that renders crisp at that resolution:

| Size | Stroke |
| ---- | ------ |
| 16   | 1.0    |
| 20   | 1.2    |
| 24   | 1.5    |
| 32   | 2.0    |
| 48   | 2.5    |
| 64   | 3.0    |
| 128  | 4.5    |
| 256  | 6.0    |

The back square's geometry is also inset by half the stroke width so the line
sits inside the nominal bounds rather than straddling the edge — which is the
other thing that fuzzes outlines at small sizes.

If you ever need to regenerate (e.g. tweak colours), `build.py` and
`build_ico.py` rebuild the entire set deterministically.

## Installation in Drover.App

### 1. Drop ICOs into resources

```
src/Drover.App/Resources/Icons/
  drover-idle.ico
  drover-working.ico
  drover-attention.ico
  drover-inactive.ico
  drover.svg                  (kept for reference, not embedded)
```

### 2. Set the app's main icon

In `Drover.App.csproj`:

```xml
<PropertyGroup>
  <ApplicationIcon>Resources\Icons\drover-idle.ico</ApplicationIcon>
</PropertyGroup>

<ItemGroup>
  <Resource Include="Resources\Icons\drover-idle.ico" />
  <Resource Include="Resources\Icons\drover-working.ico" />
  <Resource Include="Resources\Icons\drover-attention.ico" />
  <Resource Include="Resources\Icons\drover-inactive.ico" />
</ItemGroup>
```

`ApplicationIcon` sets the .exe's icon (what shows in Explorer, Alt+Tab, the
taskbar when the app is running). The `<Resource>` entries make all four ICOs
loadable at runtime via `Application.GetResourceStream`.

### 3. Wire the tray icon to attention state

The shape of the wiring matters because tray icons are GDI handles — if you
`new System.Drawing.Icon(...)` on every state change, you leak handles and
eventually your tray icon disappears (or worse, the whole app's GDI quota
runs out and chrome stops rendering).

Pre-cache the four icons once at startup, swap references on state change.

```csharp
// Services/TrayIconService.cs
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using NotifyIcon = System.Windows.Forms.NotifyIcon;

namespace Drover.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _idle;
    private readonly Icon _working;
    private readonly Icon _attention;
    private readonly Icon _inactive;

    public TrayIconService()
    {
        // Load each ICO once and cache the unmanaged handle for the
        // lifetime of the service. NotifyIcon.Icon takes a reference;
        // it does NOT clone, so we must keep these alive.
        _idle      = LoadIcon("drover-idle.ico");
        _working   = LoadIcon("drover-working.ico");
        _attention = LoadIcon("drover-attention.ico");
        _inactive  = LoadIcon("drover-inactive.ico");

        _notifyIcon = new NotifyIcon
        {
            Icon = _idle,
            Text = "Drover",
            Visible = true,
        };
    }

    public void SetState(TrayState state)
    {
        _notifyIcon.Icon = state switch
        {
            TrayState.Working   => _working,
            TrayState.Attention => _attention,
            TrayState.Inactive  => _inactive,
            _                   => _idle,
        };
    }

    private static Icon LoadIcon(string name)
    {
        // Pack URI lookup. Files declared as <Resource> in the csproj
        // are embedded in the assembly and loadable this way.
        var uri = new Uri($"pack://application:,,,/Resources/Icons/{name}",
                          UriKind.Absolute);
        var streamInfo = System.Windows.Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException($"Icon resource missing: {name}");
        using var stream = streamInfo.Stream;
        return new Icon(stream);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _idle.Dispose();
        _working.Dispose();
        _attention.Dispose();
        _inactive.Dispose();
    }
}

public enum TrayState { Idle, Working, Attention, Inactive }
```

### 4. Drive the state from existing services

`AttentionMonitor` already raises `StateChanged` per tab. Aggregate across all
tabs in `ShellViewModel`:

```csharp
// In ShellViewModel
private TrayState ComputeTrayState()
{
    if (Tabs.Count == 0) return TrayState.Inactive;
    foreach (var t in Tabs)
        if (t.Attention == AttentionState.Idle && /* was previously Working */)
            return TrayState.Attention;  // someone needs you
    foreach (var t in Tabs)
        if (t.Attention == AttentionState.Working)
            return TrayState.Working;
    return TrayState.Idle;
}
```

Subscribe to `AttentionChanged` on each `TerminalTabViewModel` (already raised
in your existing code) and call `_tray.SetState(ComputeTrayState())` whenever
any tab transitions. Debounce at ~200ms if attention thrashes — Claude's
spinner can blip Working→Idle→Working over short windows during tool calls
and you don't want the tray icon to flash.

### 5. Don't forget high-DPI

`System.Windows.Forms.NotifyIcon` picks the right size from the multi-res ICO
automatically based on the user's tray scaling. The 7 sizes embedded
(16/24/32/48/64/128/256) cover every Windows 11 DPI scaling level.

If you're on a fractional DPI (1.5x is common on 4K laptops), Windows picks
the next size up and scales down — that's why having 24 and 48 in the bundle
matters even though the "named" sizes are 16/32/64.

## Visual reference

ASCII at small sizes — the silhouette to pattern-match against in your tray:

```
Idle / Working:        Attention:           Inactive:
 ▢                      ▦                    ▫
   ◼                      ◼                    ▫
```

(Where ▢ = outlined back square, ◼ = solid front square, ▦ = filled with
matching outline, ▫ = grey muted.)

## If you need to regenerate

```bash
cd path/to/drover-icons
python3 build.py        # re-rasterise SVGs to PNGs
python3 build_ico.py    # bundle PNGs into multi-res ICOs
```

Both scripts are idempotent and overwrite existing outputs.
