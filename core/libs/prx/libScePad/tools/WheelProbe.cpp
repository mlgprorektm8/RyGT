// ryty-wheel-probe: streams the state of every joystick libScePad reads (racing wheels, pedal
// sets and other joysticks SDL does not map as game controllers) to stdout, so the launcher's
// wheel mapping window sees the same devices and the same axis/button numbers as the game.
//
// Output, one line each, flushed:
//   device <instance> <vid> <pid> <wheel 0|1> <axes> <buttons> <hats> <name>   (after "devices")
//   state <instance> <axis,axis,...> <pressed button,...|-> <hat,hat,...|->
// "devices" starts a new device list whenever one is connected or removed. The program exits
// when the process given as its first argument ends.

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#define SDL_MAIN_HANDLED
#include "SDL.h"

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif

namespace {

// Kept in step with WheelInput::IsWheel.
bool IsWheel(int index) {
    if (SDL_JoystickGetDeviceType(index) == SDL_JOYSTICK_TYPE_WHEEL) return true;
    const Uint16 vendor = SDL_JoystickGetDeviceVendor(index);
    const Uint16 product = SDL_JoystickGetDeviceProduct(index);
    if (vendor == 0x346E || vendor == 0x0EB7) return true;
    if (vendor == 0x044F) { // Thrustmaster, but not its gamepads, flight sticks and throttles
        const SDL_JoystickType type = SDL_JoystickGetDeviceType(index);
        if (type != SDL_JOYSTICK_TYPE_GAMECONTROLLER && type != SDL_JOYSTICK_TYPE_FLIGHT_STICK &&
            type != SDL_JOYSTICK_TYPE_THROTTLE && type != SDL_JOYSTICK_TYPE_ARCADE_STICK) return true;
    }
    constexpr std::array<Uint16, 11> logitech{0xC24F, 0xC266, 0xC29B, 0xC299, 0xC29A, 0xC294, 0xC298, 0xC268, 0xC262, 0xC26E, 0xC272};
    return vendor == 0x046D && std::find(logitech.begin(), logitech.end(), product) != logitech.end();
}

struct Device {
    SDL_Joystick* joystick;
    SDL_JoystickID instance;
    bool wheel;
};

std::vector<Device> devices;

void Rescan() {
    for (const auto& device : devices) SDL_JoystickClose(device.joystick);
    devices.clear();
    std::printf("devices\n");
    for (int index = 0; index < SDL_NumJoysticks(); ++index) {
        const bool wheel = IsWheel(index);
        if (!wheel && SDL_IsGameController(index)) continue;
        SDL_Joystick* joystick = SDL_JoystickOpen(index);
        if (joystick == nullptr) continue;
        const char* name = SDL_JoystickName(joystick);
        Device device{joystick, SDL_JoystickInstanceID(joystick), wheel};
        devices.push_back(device);
        std::printf("device %d %04x %04x %d %d %d %d %s\n", device.instance, SDL_JoystickGetVendor(joystick), SDL_JoystickGetProduct(joystick),
            wheel ? 1 : 0, SDL_JoystickNumAxes(joystick), SDL_JoystickNumButtons(joystick), SDL_JoystickNumHats(joystick),
            name != nullptr ? name : "unknown");
    }
    std::fflush(stdout);
}

}

int main(int argc, char** argv) {
#ifdef _WIN32
    HANDLE parent = nullptr;
    if (argc > 1) parent = OpenProcess(SYNCHRONIZE, FALSE, static_cast<DWORD>(std::strtoul(argv[1], nullptr, 10)));
#else
    (void)argc;
    (void)argv;
#endif
    // The same joystick backends as libScePad (gamepads stay with the window's controller code).
    SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
    SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI, "0");
    SDL_SetHint(SDL_HINT_JOYSTICK_RAWINPUT, "0");
    if (SDL_Init(SDL_INIT_JOYSTICK) != 0) {
        std::printf("error %s\n", SDL_GetError());
        return 1;
    }
    Rescan();
    for (;;) {
#ifdef _WIN32
        if (parent != nullptr && WaitForSingleObject(parent, 0) == WAIT_OBJECT_0) break;
#endif
        SDL_Event event;
        bool changed = false;
        while (SDL_PollEvent(&event)) {
            if (event.type == SDL_JOYDEVICEADDED || event.type == SDL_JOYDEVICEREMOVED) changed = true;
        }
        if (changed) Rescan();
        SDL_JoystickUpdate();
        for (const auto& device : devices) {
            std::string line = "state " + std::to_string(device.instance) + " ";
            const int axes = SDL_JoystickNumAxes(device.joystick);
            for (int axis = 0; axis < axes; ++axis) {
                if (axis > 0) line += ',';
                line += std::to_string(SDL_JoystickGetAxis(device.joystick, axis));
            }
            if (axes <= 0) line += '-';
            std::string pressed;
            for (int button = 0; button < SDL_JoystickNumButtons(device.joystick); ++button) {
                if (SDL_JoystickGetButton(device.joystick, button) == 0) continue;
                if (!pressed.empty()) pressed += ',';
                pressed += std::to_string(button);
            }
            line += ' ';
            line += pressed.empty() ? "-" : pressed;
            std::string hats;
            for (int hat = 0; hat < SDL_JoystickNumHats(device.joystick); ++hat) {
                if (hat > 0) hats += ',';
                hats += std::to_string(SDL_JoystickGetHat(device.joystick, hat));
            }
            line += ' ';
            line += hats.empty() ? "-" : hats;
            std::printf("%s\n", line.c_str());
        }
        std::fflush(stdout);
        SDL_Delay(50);
    }
    for (const auto& device : devices) SDL_JoystickClose(device.joystick);
    SDL_Quit();
    return 0;
}
