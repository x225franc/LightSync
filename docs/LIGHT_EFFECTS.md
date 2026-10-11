# Light effects (Toolkit plugin and standalone)

Each Yeelight/Govee card in **Lights** now has an Effect selector:
Screen Ambilight (existing default), Solid Color, Breathing, Color Cycle,
Rainbow Wave and Rainbow Wheel. Color appears for Solid/Breathing, speed for
animated effects, reverse for Wave/Wheel. Brightness remains per device.
Settings are persisted in the existing LightSync config.json.

Group and screen-canvas settings are retained while another effect is selected.
They become active again when returning to Screen Ambilight. Independent effects
work without a group, a screen frame or Chroma/Synapse. Existing configurations
without these fields keep Screen Ambilight.

Govee renders each actual segment; Wave spans the strip and Wheel wraps a hue
ring along its segments. A single-color bulb has no spatial zones, so the two
rainbow effects become a changing solid hue. This is not a two-dimensional wheel
on an ordinary strip or bulb.

All effects use the existing sending loop and Govee/Yeelight refresh settings.
Yeelight's existing command quota is preserved: animations are limited by the
configured update rate and the LAN command budget, rather than claiming keyboard
refresh rates. Breathing does not use the screen-darkness power-off threshold.
Screensaver/lock capture pauses retain the last accepted scene/gradient, and
suspend/shutdown control release and optional power actions are preserved.

Validation: both Debug applications compiled; plugin build has zero warnings;
standalone retains the existing MSB3276 dependency warning. Regression tests run
against both production engine variants with fake network transports, verifying
independent effects, time-based colors, segment gradients, reverse, screen return,
frozen keepalive, rate limits and the previous lifecycle/recovery protections.
Physical appearance of all new effects on the user's lamps needs user validation.

## Color wheel and global preset

Solid Color/Breathing use the same hue/saturation wheel as Laptop keyboard,
with a saved-color swatch. Brightness remains a separate slider.

Expand **Global effect** at the top of Lights, select effect/color/speed/reverse/
brightness, then click **Apply to all lights**. The operation applies to all
currently known Yeelight/Govee entries, including their saved settings when
unreachable. Disabled lights stay disabled. Group and canvas mappings are retained.
The global panel's last applied values are saved alongside per-light settings.
This is an explicit preset application, not a permanent override: individual
settings remain editable; subsequently discovered lights are not automatically
added to the preset until it is applied again.

Applying a global preset resets the common animation clock. Each loop iteration
uses one shared animation timestamp for all lights, so slow sends cannot change
the following lights' calculated phase within that iteration. Network latency,
per-device refresh rates and Yeelight's LAN quota still affect visible alignment.
Changing a single light afterwards does not alter the others.

Validation includes the actual WPF wheel's two-way binding and saved/external
color updates, plus global preset values/brightness, disabled-light preservation,
individual overrides and identical color calculations with delayed sends.
Both production variants pass these tests and prior engine regressions.
