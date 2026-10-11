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
