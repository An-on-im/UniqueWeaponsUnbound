# RimWorld Two-Color Mask Shader & the `Graphic` Class Family

> How `CutoutComplex` (RimWorld's two-color mask shader) works, why the engine's recolor contract
> stops at exactly two colors, how the `Graphic_*` classes differ, and why `Graphic_Random` clamps
> the second color to white. Grounded in Assembly-CSharp (1.6) disassembly + Core/DLC def data.
> Captured 2026-06-06.

This doc underpins the customization dialog's weapon **preview** (`Source/1.6/UI/Dialog_WeaponCustomization.Preview.cs`),
which recolors the weapon graphic to show the player a prospective color/texture. Getting that
faithful means understanding exactly how RimWorld colors a `Thing`.

---

## 1. The two-color mask shader

`CutoutComplex` is RimWorld's general-purpose **mask** shader. It is *not* weapon-specific — it
predates weapons by years and is shared infrastructure.

- A shader is "two-color" iff it exposes a `_MaskTex` property. `ShaderUtility.SupportsMaskTex()`
  literally introspects the Unity shader for that property at runtime. `CutoutComplex` has it;
  plain `Cutout` does not.
- Relevant shader properties (`Verse.ShaderPropertyIDs`): `_Color`, `_ColorTwo`, `_MaskTex`.
- The mask texture (`<texturename>_m`) keys regions **by channel**:
  - **Red → tinted by color one (`_Color`)**
  - **Green → tinted by color two (`_ColorTwo`)**
  - **Black → untinted (raw diffuse)** — a black mask region over the silhouette shows the
    un-tinted texture, so masks must paint the whole silhouette.
- `Graphic_Single.Init` loads the mask only when `req.shader.SupportsMaskTex()` is true, then hands
  `color`, `colorTwo`, and `maskTex` to `MaterialPool.MatFrom`.

### Why exactly two colors

Two is the ceiling of the **entire engine recolor contract**, not just this shader:

- A `Thing` exposes only `DrawColor` and `DrawColorTwo`. There is no third.
- The only generic recolor entry point is `Graphic.GetColoredVersion(Shader, Color, Color)`.

So any mod wanting more channels would have to bypass this with custom shader parameters / a bespoke
draw path — unreachable through any generic call, including the engine's own.

### The engine's per-thing recolor

`GraphicData.GraphicColoredFor(Thing t)` is the canonical recipe:

```csharp
public Graphic GraphicColoredFor(Thing t)
{
    if (ignoreThingDrawColor || (t.DrawColor.IndistinguishableFrom(Graphic.Color)
            && t.DrawColorTwo.IndistinguishableFrom(Graphic.ColorTwo)))
        return Graphic;
    return Graphic.GetColoredVersion(Graphic.Shader, t.DrawColor, t.DrawColorTwo);
}
```

Note it recolors the **top-level** graphic (so the graphic class's own `GetColoredVersion` override
runs) using `Graphic.Shader` and the Thing's two colors. The preview mirrors this.

---

## 2. Where `CutoutComplex` is used

30 def files across every DLC. In **Core** (pre-DLC, the original consumers):

- **Animals** — guinea pigs, huskies, cats, cows, pigs, big cats/birds. Coat-color variation: one
  texture tinted per-spawn with a body color + a markings color.
- **Buildings** — furniture, structures, production, natural, joy. Stuff color + an accent region.

DLC weapons (Odyssey) simply **reuse** this machinery (see §4).

---

## 3. The `Graphic_*` class family (high level)

Everything derives from the abstract `Verse.Graphic`. The ones that matter day-to-day:

| Class | Textures it loads | How it picks what to draw | Typical use |
|---|---|---|---|
| `Graphic_Single` | one texture | always the same material | non-rotatable / identical-from-all-angles items, **subgraphics of collections** |
| `Graphic_Multi` | `_north/_east/_south/_west` (4) | by the thing's `Rot4` facing (with fallbacks) | directional things: pawns, doors, beds, anything that faces |
| `Graphic_Collection` *(abstract)* | **every** texture in a folder → `subGraphics[]` | — (base for the variant pickers below) | — |
| `Graphic_Random` | folder of variants | per-thing, stable: `(overrideGraphicIndex ?? thingIDNumber) % count` | static visual variety: plants, filth, stone chunks, rubble, building variants |
| `Graphic_RandomRotated` | wraps one inner graphic | inner graphic + a per-thing random *rotation angle* | scattered debris/items that look hand-dropped |
| `Graphic_StackCount` | folder of variants | by the stack's item count | item piles that look fuller as count rises |
| `Graphic_Appearances` | per-stuff-category variants | by the thing's `Stuff` | things that look different per material |
| `Graphic_Linked` / `_LinkedCornerFiller` | a tileset | by which neighbors connect | walls, conduits, pipes — anything that joins up |

Mental model:
- **Single vs Multi** = "one look" vs "four facings."
- **Collection subclasses** = "a *bag* of looks, pick one per thing" — the differentiator is the
  *selection rule* (random-stable, rotation, stack count, stuff, neighbor-linking).
- A `Graphic_Collection` builds its `subGraphics` from a folder, each as a `Graphic_Single`
  (its `SingleGraphicType`) or `Graphic_Multi` if directional textures are present.

(There are many more — `Graphic_Mote`/`Graphic_Fleck`/`Graphic_Gas` for effects, `Graphic_Terrain`,
`Graphic_Indexed`, `Graphic_Genepack`, etc. — but the table above covers static world objects.)

---

## 4. Why `Graphic_Random` clamps `colorTwo` to white

```csharp
// Verse.Graphic_Random
public override Graphic GetColoredVersion(Shader newShader, Color newColor, Color newColorTwo)
{
    if (newColorTwo != Color.white)
        Log.ErrorOnce("Cannot use Graphic_Random.GetColoredVersion with a non-white colorTwo.", 9910251);
    return GraphicDatabase.Get<Graphic_Random>(path, newShader, drawSize, newColor, Color.white, data);
}
```

`Graphic_Single`/`Graphic_Multi` forward both colors faithfully; `Graphic_Random` hard-codes white
and warns. **Why:** `Graphic_Random` has ~241 vanilla uses, and they are almost entirely plants,
filth, stone chunks, rubble, and ancient/exotic building variants — all single-color (stuff tint) or
untinted. *No vanilla random-variant content ever needed a second tint*, so the two-color path was
simply never wired through `Graphic_Random`. The clamp + `Log.ErrorOnce` is a **defensive guard**
("random variants here aren't expected to carry a second color"), **not a technical limit**.

Proof it's only a guard: a 3-line subclass that forwards `colorTwo` works perfectly, because
`GraphicDatabase` already keys on `colorTwo` and `Graphic_Collection.Init` rebuilds the subgraphics
with whatever color it is handed:

```csharp
public class Graphic_RandomComplex : Graphic_Random
{
    public override Graphic GetColoredVersion(Shader newShader, Color newColor, Color newColorTwo)
        => GraphicDatabase.Get<Graphic_RandomComplex>(path, newShader, drawSize, newColor, newColorTwo, data);
}
```

(This is exactly what the companion mod **Unique Melee Weapons** ships, to drive a stuff tint through
the green mask channel.)

> Caveat: "deliberate guard vs. never-bothered" is inference — the DLL has no commit history — but
> the evidence is one-sided.

---

## 5. The punchline for unique weapons

Odyssey's unique weapons are the **one place in vanilla** where `Graphic_Random` meets
`CutoutComplex` in a single `graphicData` (confirmed in Odyssey `Weapons_Unique.xml`). But they only
author the **red** channel — color one comes from `CompUniqueWeapon`; they never set a second color,
so `DrawColorTwo` stays at its white default. They live *inside* the clamp's assumption, so it is a
silent no-op and they never trip the warning.

For the preview that means: don't *predict* the appearance, *ask a prospective object for it*. The
preview builds a `Thing` in the desired state (the result def, the chosen color-one, and
the desired **trait list**), then reads that thing's own `Graphic` — which resolves through
`GraphicColoredFor` using the thing's own `DrawColor`/`DrawColorTwo`. This runs the weapon's own
graphic class (a `colorTwo`-preserving `Graphic_Random` subclass) **and** its own color-two
derivation, so a downstream mod that forces color two from a trait (UMW's `ForcedColorTwoExtension`,
read in its `DrawColorTwo` override) comes through for free — no type coupling, and it generalizes to
any override reachable through the thing's graphic. See `Dialog_WeaponCustomization.Preview.cs` →
`BuildPreviewGraphic`.

> **Why a prospective Thing rather than passing `weapon.DrawColorTwo`?** An earlier version recolored
> the top-level graphic by hand and passed the *live* weapon's `DrawColorTwo`. That reads the
> **committed** weapon, not the dialog's prospective trait edit — fine while color two was only the
> (trait-independent) stuff tint, but it silently regressed the moment a downstream mod made color two
> **trait-derived**: toggling such a trait in the dialog changed nothing the live weapon reported. The
> hand-derived color one had the same blind spot — it only modeled vanilla's color-one `forcedColor`.
> Building the object in the prospective state and letting it color itself removes both the staleness
> and the per-mechanism coupling. The one ceiling: an override living purely in a draw-time patch
> (never changing the thing's queryable `Graphic`) can't be reconstructed by any approach short of
> invoking that draw path. Consequence: appearance is now trait-dependent, so the preview's render
> cache keys on the trait set, not just def + color.
>
> **The cost of building a Thing:** unlike the old graphic-only path, `ThingMaker.MakeThing` mutates
> *global* sim state — `Thing.PostMake` pulls a `UniqueIDsManager` id and `PostPostMake` rolls random
> traits/name/color off the global `Rand`. Because preview rebuilds run during GUI layout (off the
> synchronized tick), unguarded that is a multiplayer-desync hazard. Two guards contain it: the make
> is wrapped in `Rand.Push/PopState` (the throwaway rolls don't perturb the shared Rand stream), and
> the Thing is cached and re-made only on def change (the id draw fires per def, not per rebuild).
