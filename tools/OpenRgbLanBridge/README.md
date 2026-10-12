# OpenRGB -> LightSync LAN bridge proof of concept

Target: Govee H6061, 192.168.0.153, ten independent segments confirmed visually.
The existing laptop-keyboard screen mapping is untouched. This prototype is separate
from the LightSync engine; it validates the transport before production integration.

OpenRGB acts as SDK CLIENT. Connect to 127.0.0.1:6743 in its SDK Client tab.
A device named LightSync Bridge - Govee H6061 (10 segments) should appear.
Assign only that device to an effect, or add its Panels zone to Visual Map.
Keep native OpenRGB Govee control and LightSync control of this target disabled.

Start: python tools/OpenRgbLanBridge/bridge.py
Stop: disconnect the SDK Client, then stop the process or create a file named STOP
beside bridge.py. The server also exits after 30 minutes. The UDP control session
is released on disconnect/shutdown. No lamp power or brightness commands are sent.
The sender uses LightSync's Govee segment frame format, a single latest-color buffer,
a maximum 20 Hz output and 1 Hz unchanged-frame keepalive. No delayed frame queue.

Run protocol tests: python tools/OpenRgbLanBridge/test_bridge.py
The tests validate packing, RGB byte order, XOR checksum and malformed update sizes.
Actual OpenRGB client compatibility and effect behavior require the live test.

Inventory supplied by the user:
- H61A0 x2: 192.168.0.16 and 192.168.0.227
- H6061: 192.168.0.153 (current target)
- H6056: 192.168.0.124
- Yeelight color x3: 192.168.0.202, 192.168.0.25, 192.168.0.54

Future integration: the standalone and Toolkit LightSync plugin should share the
bridge implementation and reuse their current LAN session/recovery handling.
The laptop keyboard must keep its existing whole-screen source unless explicitly
assigned a new source by the user.

Live validation: OpenRGB SDK Client discovered all ten panels; Ambient streamed
colors successfully. The user confirmed independent screen-following panel colors.
The bridge log recorded more than 300 RGB updates without a reported error.
This validates the H6061 prototype only; other models and production integration
remain to be implemented and tested.
