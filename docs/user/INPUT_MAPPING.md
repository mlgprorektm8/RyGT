# Controller, keyboard and mouse input

Ryty accepts SDL-mapped game controllers as well as its built-in keyboard and mouse bindings. Xbox-style controllers and PlayStation controllers recognized by SDL use the standard PS button layout; sticks and analog triggers are passed through, and controllers can be connected or disconnected while the game is running. The first recognized controller is used. Ryty uses its built-in keyboard and mouse bindings when no configuration file is present. To change selected keyboard and mouse bindings, create `ryty-input.ini` beside the generated game executable. Set `RTY_INPUT_CONFIG` (legacy `ANYPS5_INPUT_CONFIG` is still honored) to use a file at another path.

For an Xbox-layout controller, A/B/X/Y map to Cross/Circle/Square/Triangle, LB/RB map to L1/R1, and LT/RT map to analog L2/R2. Start maps to Options. D-pad and stick-click buttons are supported. On a PlayStation controller, the touchpad click button is forwarded; finger touch coordinates are not implemented.

Each non-empty line has the form `Action = Type:Value`. Action names are case-insensitive. A `#` or `;` starts a comment. The first line for an action replaces its built-in bindings; later lines for the same action add alternate inputs. Duplicate entries for the same action are ignored. Actions omitted from the file keep their built-in bindings. UTF-8 files with or without a byte-order mark are supported.

Supported input sources are:

- `KEY:Return`, `KEY:Space`, or another key name accepted by SDL.
- `MOUSE:Left`, `MOUSE:Middle`, `MOUSE:Right`, `MOUSE:X1`, or `MOUSE:X2`.
- `WHEEL:Up` or `WHEEL:Down` for pad buttons.

Game-controller bindings are enabled automatically and are not affected by keyboard or mouse overrides in this file.

Supported actions are `Cross`, `Circle`, `Triangle`, `Square`, `L1`, `R1`, `L2`, `R2`, `L3`, `R3`, `Options`, `Up`, `Right`, `Down`, `Left`, `LeftStickLeft`, `LeftStickRight`, `LeftStickUp`, `LeftStickDown`, `RightStickLeft`, `RightStickRight`, `RightStickUp`, `RightStickDown`, `TouchLeft`, `TouchRight`, `ToggleMouse`, and `ToggleFullscreen`.

For example, this changes Cross to F or Space, moves the left stick to IJKL, uses the mouse buttons for Square and R2, and keeps all other built-in bindings:

```ini
Cross = KEY:F
Cross = KEY:Space
LeftStickLeft = KEY:J
LeftStickRight = KEY:L
LeftStickUp = KEY:I
LeftStickDown = KEY:K
Square = MOUSE:Left
R2 = MOUSE:Right
ToggleMouse = MOUSE:Middle
```

An invalid line reports the file and line number and stops input initialization. If the configured file does not exist or cannot be read, Ryty reports an error. With no `ryty-input.ini` and no `RTY_INPUT_CONFIG`, the built-in mapping is used.

## Racing wheels and pedals

Racing wheels (Logitech G25/G27/G29/G920/G923/G PRO, Moza, Fanatec, Thrustmaster (T150, T248, T300, TX, TS-XW, T-GT, T818 and the like; not its gamepads or flight sticks) and any device SDL reports as a wheel) and other joysticks that SDL does not map as game controllers are read by libScePad on their own thread, whichever window or renderer is in use.

The wheel is reported to the game as a native steering wheel: `scePadOpenExt` opens it, `scePadReadExt`/`scePadReadStateExt` read it, and `scePadDeviceClassParseData` reports its position, throttle, brake and clutch through the steering-wheel device class. By default it presents itself as a Fanatec direct-drive base (`0eb7:0e05`), which GT Sport drives through its Fanatec SDK; it can present itself as a Logitech G29 (`046d:c24f`) instead, which GT Sport drives through its `logiWheel.prx`. The wheel's mapped buttons also reach player 1's ordinary pad, so menus can be used with it.

Without any configuration:

- Steering is axis 0 of the wheel.
- The pedals are found automatically: axes that rest at one end of their range, on the wheel first and then on a separately connected pedal set, become the throttle, brake and clutch, in axis order. Pedals that rest high (Logitech) and pedals that rest low (Moza, most others) both work.
- The wheel's D-pad is the pad's D-pad. On Logitech wheels the face buttons map to the matching PlayStation buttons (or A/B/X/Y on Xbox wheels), the right and left paddles to R1 and L1, and Options/Menu to Options. Other wheels have no default button mapping.
- Wheels with force feedback get a centering spring at 30% strength until the game sends its own forces. Set `RTY_WHEEL_SPRING` to a value from 0 to 100 to change it; 0 turns it off.

When a wheel connects, the log lists it with its axis, button and hat counts and shows which axes became steering, throttle, brake and clutch. The first time each axis is moved and each button is pressed, the log names it, so the numbers for the settings below can be found by moving each control once.

### ryty-input.ini

Wheel inputs can be set with these sources:

- `JOYAXIS:<n>` for `Steering`, `Throttle`, `Brake` or `Clutch`. Add `:INVERT` to reverse the steering direction, or the direction of a pedal that rests in the middle of its range. Pedals that rest at one end find their direction automatically.
- `JOYBUTTON:<n>` for any pad button or stick direction.
- `JOYHAT:Up`, `JOYHAT:Down`, `JOYHAT:Left` or `JOYHAT:Right` for any pad button or stick direction.

Any of them can end with `@<name>` to pick a device whose name contains `<name>` (case-insensitive), for example a pedal set connected by its own USB cable. Without it, the wheel is used. A joystick line replaces only the built-in wheel binding for that action; keyboard and mouse bindings for the same action stay. For example, for a Moza R3:

```ini
Cross = JOYBUTTON:0
Circle = JOYBUTTON:1
Square = JOYBUTTON:2
Triangle = JOYBUTTON:3
Up = JOYBUTTON:4
Right = JOYBUTTON:5
Down = JOYBUTTON:6
Left = JOYBUTTON:7
Options = JOYBUTTON:36
R1 = JOYBUTTON:13
L1 = JOYBUTTON:12
Throttle = JOYAXIS:1
Brake = JOYAXIS:4
```

The numbers above are an example; use the numbers your log shows.

### ryty-wheel.ini

Optional, beside the game executable, with `Key = Value` lines:

| Key | Default | Meaning |
| --- | --- | --- |
| `Rotation` | 900 | Lock-to-lock degrees, as set in the wheel's own software. |
| `PedalMax` | 65535 | Pedal value reported at full press. |
| `AngleUnit` | `deg` | Unit of the reported steering angle (`deg` or `rad`). |
| `Present` | `0eb7:0e05` | USB vendor:product the wheel is presented as: `0eb7:0e05` a Fanatec direct-drive base, `046d:c24f` a Logitech G29. |
| `Capability` | 0 | Steering-wheel capability bits reported to the game. |
| `FfbGain` | 100 | Force feedback strength in percent (0-200). |
| `FfbInvert` | 0 | 1 reverses the direction of constant forces. |
| `FfbDamping` | 10 | Damping against the wheel's speed, in percent (0-100), added to the constant force; raise it if the wheel oscillates on straights or when parked. |
| `FfbDetail` | 0 | Extra force for quick changes such as kerbs and bumps, in percent (0-200); the steady force is unchanged. |
| `PadIsWheel` | 0 | 1 makes player 1's pad report itself as the steering wheel too. Leave it at 0: with it on, GT Sport sends no driving forces through logiWheel and ignores the wheel in a race. |

### Force feedback

`logiWheel.prx` sends a G29's force feedback as output report 0x30 through `scePadOutputReport`: a report-id byte followed by Logitech classic 8-byte commands. libScePad tracks the four effect slots (constant, variable, spring with its force cap, damper, friction, play/stop, autocenter) and plays them on the wheel as SDL haptic effects. logiWheel's report thread runs on a kernel timer event (`sceKernelAddTimerEvent`). With `Present = 046d:c24f` (and `PadIsWheel = 0`, `Capability = 0`) GT Sport drives the wheel through logiWheel and sends its road forces, in 8-bit steps.

### Presenting a Fanatec wheel (default)

With `Present = 0eb7:0e05` GT Sport uses its Fanatec SDK. The SDK opens the wheel with `scePadOpenExt`, reads its configuration (feature report 0x31: rim, pedals, firmware) and rim inputs (feature report 0x33), and sends one 7-byte slot command per output report 0x30 (constant force in 16 bits, spring, damper, friction), which libScePad decodes into the wheel's forces. In a race the game's Fanatec port reads the wheel from the pad data's device-unique block, which libScePad fills: steering, accelerator and brake as 16-bit values (pedals released at 0xffff), handbrake and clutch bytes, and the H-shifter gear. Keep `PadIsWheel = 0`.
