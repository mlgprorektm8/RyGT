#include "prx/libScePad/include/WheelInput.hpp"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdlib>
#include <cstring>

#include "SDL.h"
#include "prx/libc/include/General.hpp"

namespace {

constexpr std::uint16_t VendorLogitech = 0x046D;
constexpr std::uint16_t VendorFanatec = 0x0EB7;
constexpr std::uint16_t VendorMoza = 0x346E;
constexpr std::uint16_t VendorThrustmaster = 0x044F;

// Logitech wheels in PlayStation mode (G29, G923 PS, G27, G25, Driving Force GT, G PRO PS).
constexpr std::array LogitechPlayStationWheels{
    std::uint16_t{0xC24F}, std::uint16_t{0xC266}, std::uint16_t{0xC29B}, std::uint16_t{0xC299},
    std::uint16_t{0xC29A}, std::uint16_t{0xC294}, std::uint16_t{0xC298}, std::uint16_t{0xC268}
};
// Logitech wheels in Xbox mode (G920, G923 Xbox, G PRO Xbox).
constexpr std::array LogitechXboxWheels{std::uint16_t{0xC262}, std::uint16_t{0xC26E}, std::uint16_t{0xC272}};

template <std::size_t N>
bool contains(const std::array<std::uint16_t, N>& list, std::uint16_t value) {
    return std::find(list.begin(), list.end(), value) != list.end();
}

bool isLogitechWheel(std::uint16_t vendor, std::uint16_t product) {
    return vendor == VendorLogitech && (contains(LogitechPlayStationWheels, product) || contains(LogitechXboxWheels, product));
}

// Thrustmaster makes wheels (T150, T248, T300, TX, TS-XW, T-GT, T818...) and also gamepads,
// flight sticks and throttles: any of its devices that SDL does not take for one of those counts.
bool isThrustmasterWheel(std::uint16_t vendor, SDL_JoystickType type) {
    return vendor == VendorThrustmaster && type != SDL_JOYSTICK_TYPE_GAMECONTROLLER && type != SDL_JOYSTICK_TYPE_FLIGHT_STICK &&
           type != SDL_JOYSTICK_TYPE_THROTTLE && type != SDL_JOYSTICK_TYPE_ARCADE_STICK;
}

std::string lower(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}

// 0..1 travel of a pedal axis from its resting position. A pedal that rests at one end of the
// range is read towards the other end (Logitech pedals rest high, most others rest low); one
// that rests near the middle is read towards the positive end, or the negative end if inverted.
float pedalAmount(int value, int rest, bool invert) {
    float amount;
    if (rest > 8000) amount = static_cast<float>(rest - value) / static_cast<float>(rest + 32768);
    else if (rest < -8000) amount = static_cast<float>(value - rest) / static_cast<float>(32767 - rest);
    else if (!invert) amount = static_cast<float>(value - rest) / static_cast<float>(32767 - rest);
    else amount = static_cast<float>(rest - value) / static_cast<float>(rest + 32768);
    // A little dead travel at both ends so a noisy pedal rests at 0 and a worn one still reaches 1.
    return std::clamp((amount - 0.02f) / 0.95f, 0.0f, 1.0f);
}

void applyStick(Pad::InputControl control, std::array<bool, 4>& negative, std::array<bool, 4>& positive) {
    switch (control) {
        case Pad::InputControl::LeftStickLeft: negative[0] = true; break;
        case Pad::InputControl::LeftStickRight: positive[0] = true; break;
        case Pad::InputControl::LeftStickUp: negative[1] = true; break;
        case Pad::InputControl::LeftStickDown: positive[1] = true; break;
        case Pad::InputControl::RightStickLeft: negative[2] = true; break;
        case Pad::InputControl::RightStickRight: positive[2] = true; break;
        case Pad::InputControl::RightStickUp: negative[3] = true; break;
        case Pad::InputControl::RightStickDown: positive[3] = true; break;
        default: break;
    }
}

}

WheelInput::WheelInput(const Pad::InputConfig& config)
    : userBindings(config.joystickBindings), userControls(config.joystickControls), userButtons(config.joystickButtons) {
    if (const char* spring = std::getenv("RTY_WHEEL_SPRING")) springStrength = std::clamp(std::atoi(spring), 0, 100);
    SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
    if ((SDL_WasInit(SDL_INIT_JOYSTICK) & SDL_INIT_JOYSTICK) == 0 && SDL_InitSubSystem(SDL_INIT_JOYSTICK) != 0) {
        APS5_LOG_ERR("Pad: SDL joystick init failed: %s", SDL_GetError());
        return;
    }
    if (springStrength > 0 && (SDL_WasInit(SDL_INIT_HAPTIC) & SDL_INIT_HAPTIC) == 0 && SDL_InitSubSystem(SDL_INIT_HAPTIC) != 0) {
        APS5_LOG_OUT("Pad: SDL haptic init failed, wheels will not self-center: %s", SDL_GetError());
    }
    for (int deviceIndex = 0; deviceIndex < SDL_NumJoysticks(); ++deviceIndex) open(deviceIndex);
}

WheelInput::~WheelInput() {
    while (!devices.empty()) close(devices.back()->instanceId);
}

bool WheelInput::IsWheel(int deviceIndex) {
    if (SDL_JoystickGetDeviceType(deviceIndex) == SDL_JOYSTICK_TYPE_WHEEL) return true;
    const std::uint16_t vendor = SDL_JoystickGetDeviceVendor(deviceIndex);
    const std::uint16_t product = SDL_JoystickGetDeviceProduct(deviceIndex);
    return vendor == VendorMoza || vendor == VendorFanatec || isLogitechWheel(vendor, product) ||
           isThrustmasterWheel(vendor, SDL_JoystickGetDeviceType(deviceIndex));
}

void WheelInput::open(int deviceIndex) {
    if (!IsWheel(deviceIndex) && SDL_IsGameController(deviceIndex)) return;
    const SDL_JoystickID instanceId = SDL_JoystickGetDeviceInstanceID(deviceIndex);
    if (std::any_of(devices.begin(), devices.end(), [instanceId](const auto& device) { return device->instanceId == instanceId; })) return;
    SDL_Joystick* joystick = SDL_JoystickOpen(deviceIndex);
    if (joystick == nullptr) {
        APS5_LOG_ERR("Pad: could not open joystick %d: %s", deviceIndex, SDL_GetError());
        return;
    }
    auto device = std::make_unique<Device>();
    device->joystick = joystick;
    device->instanceId = SDL_JoystickInstanceID(joystick);
    const char* name = SDL_JoystickName(joystick);
    device->name = name != nullptr ? name : "unknown";
    device->lowerName = lower(device->name);
    device->vendor = SDL_JoystickGetVendor(joystick);
    device->product = SDL_JoystickGetProduct(joystick);
    device->wheel = IsWheel(deviceIndex);
    const int axes = std::max(0, SDL_JoystickNumAxes(joystick));
    device->rest.assign(static_cast<std::size_t>(axes), 0);
    device->axisLogged.assign(static_cast<std::size_t>(axes), false);
    device->buttonLogged.assign(static_cast<std::size_t>(std::max(0, SDL_JoystickNumButtons(joystick))), false);
    // DirectInput reports zeros until the first poll, so the resting positions are read a moment later.
    device->restAt = std::chrono::steady_clock::now() + std::chrono::milliseconds(300);
    APS5_LOG_OUT("Pad: connected %s: %s (%04x:%04x, %d axes, %d buttons, %d hats)", device->wheel ? "wheel" : "joystick",
        device->name.c_str(), device->vendor, device->product, axes, SDL_JoystickNumButtons(joystick), SDL_JoystickNumHats(joystick));
    startSpring(*device);
    devices.push_back(std::move(device));
    rebuild();
}

void WheelInput::startSpring(Device& device) {
    if (!device.wheel || SDL_JoystickIsHaptic(device.joystick) != SDL_TRUE) return;
    device.haptic = SDL_HapticOpenFromJoystick(device.joystick);
    if (device.haptic == nullptr) return;
    const unsigned int features = SDL_HapticQuery(device.haptic);
    device.hapticFeatures = features;
    const int axes = SDL_HapticNumAxes(device.haptic);
    APS5_LOG_OUT("Pad: wheel force feedback: %d axes, effects 0x%x", axes, features);
    if (springStrength <= 0) return;
    if ((features & SDL_HAPTIC_SPRING) != 0) {
        // Some drivers reject a condition effect without an explicit direction, so a second
        // attempt names the steering axis.
        for (int attempt = 0; attempt < 2; ++attempt) {
            SDL_HapticEffect effect;
            std::memset(&effect, 0, sizeof(effect));
            effect.type = SDL_HAPTIC_SPRING;
            effect.condition.length = SDL_HAPTIC_INFINITY;
            if (attempt == 1) {
                effect.condition.direction.type = SDL_HAPTIC_CARTESIAN;
                effect.condition.direction.dir[0] = 1;
            }
            const auto coefficient = static_cast<Sint16>(0x7FFF * springStrength / 100);
            for (int axis = 0; axis < 3; ++axis) {
                effect.condition.right_sat[axis] = 0xFFFF;
                effect.condition.left_sat[axis] = 0xFFFF;
                effect.condition.right_coeff[axis] = coefficient;
                effect.condition.left_coeff[axis] = coefficient;
            }
            device.springEffect = SDL_HapticNewEffect(device.haptic, &effect);
            if (device.springEffect >= 0 && SDL_HapticRunEffect(device.haptic, device.springEffect, 1) == 0) {
                device.conditionNeedsDirection = attempt == 1;
                APS5_LOG_OUT("Pad: wheel centering spring at %d%%", springStrength);
                return;
            }
            APS5_LOG_OUT("Pad: wheel spring attempt %d failed: %s", attempt + 1, SDL_GetError());
            if (device.springEffect >= 0) SDL_HapticDestroyEffect(device.haptic, device.springEffect);
            device.springEffect = -1;
        }
    }
    if ((features & SDL_HAPTIC_AUTOCENTER) != 0 && SDL_HapticSetAutocenter(device.haptic, springStrength) == 0) {
        APS5_LOG_OUT("Pad: wheel autocenter at %d%%", springStrength);
        return;
    }
    APS5_LOG_OUT("Pad: wheel has no centering spring: %s", SDL_GetError());
}

void WheelInput::close(SDL_JoystickID instanceId) {
    const auto it = std::find_if(devices.begin(), devices.end(), [instanceId](const auto& device) { return device->instanceId == instanceId; });
    if (it == devices.end()) return;
    Device& device = **it;
    APS5_LOG_OUT("Pad: disconnected %s", device.name.c_str());
    if (device.haptic != nullptr) {
        for (const int effect : {device.springEffect, device.constantEffect, device.gameSpringEffect, device.damperEffect, device.frictionEffect}) {
            if (effect >= 0) SDL_HapticDestroyEffect(device.haptic, effect);
        }
        SDL_HapticClose(device.haptic);
    }
    SDL_JoystickClose(device.joystick);
    devices.erase(it);
    rebuild();
}

WheelInput::Device* WheelInput::primary() const {
    for (const auto& device : devices) {
        if (device->wheel) return device.get();
    }
    for (const auto& device : devices) {
        if (!device->rest.empty()) return device.get();
    }
    return nullptr;
}

bool WheelInput::userOverrides(const Pad::JoystickBinding& binding) const {
    if (binding.control == Pad::InputControl::Button) {
        return std::find(userButtons.begin(), userButtons.end(), binding.button) != userButtons.end();
    }
    return std::find(userControls.begin(), userControls.end(), binding.control) != userControls.end();
}

void WheelInput::addDefaults(Device& wheel, std::vector<ActiveBinding>& out) const {
    const auto add = [this, &out](Device& device, Pad::JoystickBinding binding) {
        if (!userOverrides(binding)) out.push_back({std::move(binding), &device});
    };
    using Pad::InputControl;
    using Pad::PadButton;
    if (!wheel.rest.empty()) add(wheel, {InputControl::Steering, PadButton::None, 0});
    add(wheel, {InputControl::Button, PadButton::Up, -1, -1, SDL_HAT_UP});
    add(wheel, {InputControl::Button, PadButton::Down, -1, -1, SDL_HAT_DOWN});
    add(wheel, {InputControl::Button, PadButton::Left, -1, -1, SDL_HAT_LEFT});
    add(wheel, {InputControl::Button, PadButton::Right, -1, -1, SDL_HAT_RIGHT});

    const auto button = [&add, &wheel](int index, PadButton target) { add(wheel, {InputControl::Button, target, -1, index}); };
    if (wheel.vendor == VendorLogitech && contains(LogitechPlayStationWheels, wheel.product)) {
        button(0, PadButton::Cross);
        button(1, PadButton::Square);
        button(2, PadButton::Circle);
        button(3, PadButton::Triangle);
        button(4, PadButton::R1);
        button(5, PadButton::L1);
        button(9, PadButton::Options);
        button(10, PadButton::R3);
        button(11, PadButton::L3);
    } else if (wheel.vendor == VendorLogitech && contains(LogitechXboxWheels, wheel.product)) {
        button(0, PadButton::Cross);
        button(1, PadButton::Circle);
        button(2, PadButton::Square);
        button(3, PadButton::Triangle);
        button(4, PadButton::R1);
        button(5, PadButton::L1);
        button(6, PadButton::Options);
        button(8, PadButton::R3);
        button(9, PadButton::L3);
    }

    // Pedals: axes that rest at one end of their range, on the wheel first and then on any
    // separate pedal set, in axis order: throttle, brake, then clutch.
    std::vector<std::pair<Device*, int>> pedals;
    const auto collect = [&pedals](Device& device, int skipAxis) {
        if (!device.restCaptured) return;
        for (int axis = 0; axis < static_cast<int>(device.rest.size()); ++axis) {
            if (axis != skipAxis && std::abs(static_cast<int>(device.rest[axis])) > 30000) pedals.emplace_back(&device, axis);
        }
    };
    collect(wheel, 0);
    for (const auto& device : devices) {
        if (device.get() != &wheel) collect(*device, -1);
    }
    if (pedals.size() > 0) add(*pedals[0].first, {InputControl::Throttle, PadButton::None, pedals[0].second});
    if (pedals.size() > 1) add(*pedals[1].first, {InputControl::Brake, PadButton::None, pedals[1].second});
    if (pedals.size() > 2) add(*pedals[2].first, {InputControl::Clutch, PadButton::None, pedals[2].second});
}

void WheelInput::rebuild() {
    active.clear();
    Device* wheel = primary();
    for (const auto& binding : userBindings) {
        Device* device = nullptr;
        if (binding.device.empty()) {
            device = wheel;
        } else {
            const auto wanted = lower(binding.device);
            for (const auto& candidate : devices) {
                if (candidate->lowerName.find(wanted) != std::string::npos) { device = candidate.get(); break; }
            }
        }
        if (device != nullptr) active.push_back({binding, device});
    }
    if (wheel != nullptr) addDefaults(*wheel, active);

    const auto describe = [this](Pad::InputControl control, const char* label) {
        for (const auto& entry : active) {
            if (entry.binding.control != control || entry.binding.axis < 0) continue;
            APS5_LOG_OUT("Pad: %s = axis %d of %s", label, entry.binding.axis, entry.device->name.c_str());
            return;
        }
    };
    describe(Pad::InputControl::Steering, "steering");
    describe(Pad::InputControl::Throttle, "throttle");
    describe(Pad::InputControl::Brake, "brake");
    describe(Pad::InputControl::Clutch, "clutch");
}

void WheelInput::HandleEvent(const SDL_Event& event) {
    if (event.type == SDL_JOYDEVICEADDED) open(event.jdevice.which);
    else if (event.type == SDL_JOYDEVICEREMOVED) close(event.jdevice.which);
}

// Logs the first real movement of each axis, button and hat so the numbers to put in
// ryty-input.ini can be read from the log.
void WheelInput::logActivity(Device& device) {
    if (!device.restCaptured) return;
    for (std::size_t axis = 0; axis < device.rest.size(); ++axis) {
        if (device.axisLogged[axis]) continue;
        const int value = SDL_JoystickGetAxis(device.joystick, static_cast<int>(axis));
        if (std::abs(value - device.rest[axis]) < 16000) continue;
        device.axisLogged[axis] = true;
        APS5_LOG_OUT("Pad: %s axis %zu moved (rests at %d)", device.name.c_str(), axis, device.rest[axis]);
    }
    for (std::size_t button = 0; button < device.buttonLogged.size(); ++button) {
        if (device.buttonLogged[button] || SDL_JoystickGetButton(device.joystick, static_cast<int>(button)) == 0) continue;
        device.buttonLogged[button] = true;
        APS5_LOG_OUT("Pad: %s button %zu pressed", device.name.c_str(), button);
    }
    if (!device.hatLogged && SDL_JoystickNumHats(device.joystick) > 0 && SDL_JoystickGetHat(device.joystick, 0) != SDL_HAT_CENTERED) {
        device.hatLogged = true;
        APS5_LOG_OUT("Pad: %s hat pressed", device.name.c_str());
    }
}

bool WheelInput::Update() {
    if (devices.empty()) {
        std::lock_guard lock(sampleMutex);
        const bool changed = sample != Sample{};
        sample = {};
        return changed;
    }
    SDL_JoystickUpdate();
    const auto now = std::chrono::steady_clock::now();
    bool captured = false;
    for (const auto& device : devices) {
        // Wait for the driver's first real report (an axis away from zero), or give up after two
        // seconds for a device whose axes really all rest at zero.
        const auto reported = [&device] {
            for (std::size_t axis = 0; axis < device->rest.size(); ++axis) {
                if (SDL_JoystickGetAxis(device->joystick, static_cast<int>(axis)) != 0) return true;
            }
            return false;
        };
        if (!device->restCaptured && now >= device->restAt && (reported() || now >= device->restAt + std::chrono::milliseconds(1700))) {
            for (std::size_t axis = 0; axis < device->rest.size(); ++axis) {
                device->rest[axis] = SDL_JoystickGetAxis(device->joystick, static_cast<int>(axis));
            }
            device->restCaptured = true;
            captured = true;
        }
        logActivity(*device);
    }
    if (captured) rebuild();

    Sample next;
    for (const auto& entry : active) {
        const auto& binding = entry.binding;
        SDL_Joystick* joystick = entry.device->joystick;
        if (binding.axis >= 0) {
            if (binding.axis >= static_cast<int>(entry.device->rest.size())) continue;
            const int value = SDL_JoystickGetAxis(joystick, binding.axis);
            if (binding.control == Pad::InputControl::Steering) {
                const int position = binding.invert ? -1 - value : value;
                next.hasSteering = true;
                next.steering = std::clamp(position, -32768, 32767);
                continue;
            }
            if (!entry.device->restCaptured) continue;
            const auto amount = static_cast<std::uint16_t>(std::lround(pedalAmount(value, entry.device->rest[binding.axis], binding.invert) * 65535.0f));
            if (binding.control == Pad::InputControl::Throttle) next.throttle = std::max(next.throttle, amount);
            if (binding.control == Pad::InputControl::Brake) next.brake = std::max(next.brake, amount);
            if (binding.control == Pad::InputControl::Clutch) next.clutch = std::max(next.clutch, amount);
            continue;
        }
        bool down = false;
        if (binding.joystickButton >= 0) down = SDL_JoystickGetButton(joystick, binding.joystickButton) != 0;
        else if (binding.hat != 0 && SDL_JoystickNumHats(joystick) > 0) down = (SDL_JoystickGetHat(joystick, 0) & binding.hat) != 0;
        if (!down) continue;
        if (binding.control == Pad::InputControl::Button) next.buttons |= static_cast<std::uint32_t>(binding.button);
        else if (binding.control == Pad::InputControl::TouchLeft) next.touchLeft = true;
        else if (binding.control == Pad::InputControl::TouchRight) next.touchRight = true;
        else applyStick(binding.control, next.negative, next.positive);
    }
    WheelForces wanted;
    {
        std::lock_guard lock(forcesMutex);
        wanted = forces;
    }
    if (Device* wheel = primary(); wheel != nullptr && wheel->haptic != nullptr && wanted.active) playForces(*wheel, wanted, next.steering);
    std::lock_guard lock(sampleMutex);
    const bool changed = next != sample;
    sample = next;
    return changed;
}

void WheelInput::Apply(PadInputState& state) const {
    std::lock_guard lock(sampleMutex);
    state.buttons |= sample.buttons;
    for (std::size_t axis = 0; axis < state.sticks.size(); ++axis) {
        if (sample.negative[axis] || sample.positive[axis]) {
            state.sticks[axis] = sample.negative[axis] == sample.positive[axis] ? 128 : sample.negative[axis] ? 0 : 255;
        }
    }
    state.touchLeft = state.touchLeft || sample.touchLeft;
    state.touchRight = state.touchRight || sample.touchRight;
}

WheelState WheelInput::State() const {
    std::lock_guard lock(sampleMutex);
    WheelState state;
    state.connected = sample.hasSteering;
    state.steering = sample.steering;
    state.throttle = sample.throttle;
    state.brake = sample.brake;
    state.clutch = sample.clutch;
    state.buttons = sample.buttons;
    return state;
}

void WheelInput::SetForces(const WheelForces& wanted) {
    std::lock_guard lock(forcesMutex);
    forces = wanted;
}

namespace {

// Creates or updates one infinite effect; returns its id, or -1 if the wheel refuses it.
int playEffect(SDL_Haptic* haptic, int id, SDL_HapticEffect& effect, bool& running) {
    if (id >= 0) {
        if (SDL_HapticUpdateEffect(haptic, id, &effect) == 0) return id;
        SDL_HapticDestroyEffect(haptic, id);
        running = false;
    }
    id = SDL_HapticNewEffect(haptic, &effect);
    if (id < 0) return -1;
    if (SDL_HapticRunEffect(haptic, id, 1) != 0) {
        SDL_HapticDestroyEffect(haptic, id);
        return -1;
    }
    running = true;
    return id;
}

SDL_HapticEffect conditionEffect(Uint16 type, bool direction, int coefficient, int center, int limit = 0xFFFF) {
    SDL_HapticEffect effect;
    std::memset(&effect, 0, sizeof(effect));
    effect.type = type;
    effect.condition.length = SDL_HAPTIC_INFINITY;
    if (direction) {
        effect.condition.direction.type = SDL_HAPTIC_CARTESIAN;
        effect.condition.direction.dir[0] = 1;
    }
    const auto k = static_cast<Sint16>(std::clamp(coefficient, 0, 32767));
    for (int axis = 0; axis < 3; ++axis) {
        effect.condition.right_sat[axis] = static_cast<Uint16>(std::clamp(limit, 0, 0xFFFF));
        effect.condition.left_sat[axis] = static_cast<Uint16>(std::clamp(limit, 0, 0xFFFF));
        effect.condition.right_coeff[axis] = k;
        effect.condition.left_coeff[axis] = k;
        effect.condition.center[axis] = static_cast<Sint16>(std::clamp(center, -32767, 32767));
    }
    return effect;
}

}

// Plays the title's forces on the wheel, changing an effect only when its value moved.
void WheelInput::playForces(Device& device, const WheelForces& wanted, int steering) {
    if (device.springEffect >= 0) {
        // The title drives the wheel now; its own springs replace the startup one.
        SDL_HapticDestroyEffect(device.haptic, device.springEffect);
        device.springEffect = -1;
        APS5_LOG_OUT("Pad: wheel force feedback taken over by the %s", "game");
    }
    const auto& settings = Pad::Wheel();
    const int gain = std::clamp(settings.ffbGain, 0, 200);
    const auto scale = [gain](int value) { return value * gain / 100; };
    const auto moved = [](int a, int b) { return std::abs(a - b) > 96 || ((a == 0) != (b == 0)); };
    bool running = false;

    // Detail: quick changes (kerbs, bumps) are the difference between the force and its smoothed
    // value (about 120 ms at this 4 ms update); they are added again, scaled, on top.
    device.slowConstant += (static_cast<float>(wanted.constant) - device.slowConstant) * 0.033f;
    const int detail = static_cast<int>((static_cast<float>(wanted.constant) - device.slowConstant) * static_cast<float>(settings.ffbDetail) / 100.0f);
    // Damping, added to the constant force rather than asked of the wheel's driver: a force
    // against the steering's speed. The title's force lags the wheel a little, which makes a
    // strong wheel oscillate around the centre; damping settles it. Its direction follows how the
    // title's own centering force relates to the steering (they have opposite signs when a
    // positive force turns the wheel towards positive steering).
    const auto now = std::chrono::steady_clock::now();
    if (device.lastSteeringAt != std::chrono::steady_clock::time_point{}) {
        const float seconds = std::chrono::duration<float>(now - device.lastSteeringAt).count();
        if (seconds > 0.0005f && seconds < 0.1f) {
            const float speed = static_cast<float>(steering - device.lastSteering) / seconds;
            device.steeringSpeed += (speed - device.steeringSpeed) * 0.5f;
        }
    }
    device.lastSteering = steering;
    device.lastSteeringAt = now;
    device.centering += (static_cast<float>(wanted.constant) / 32767.0f * static_cast<float>(steering) / 32767.0f - device.centering) * 0.002f;
    int damping = 0;
    if (settings.ffbDamping > 0 && std::abs(device.centering) > 0.0005f) {
        // At 100%, a wheel turning at 90 degrees a second (on a 900 degree wheel) meets a third of full force.
        const float perSpeed = 0.33f * 32767.0f / 6553.6f * static_cast<float>(settings.ffbDamping) / 100.0f;
        const float direction = device.centering < 0.0f ? -1.0f : 1.0f;
        damping = static_cast<int>(std::clamp(direction * device.steeringSpeed * perSpeed, -16384.0f, 16384.0f));
    }
    const int constant = std::clamp(wanted.constant + detail + damping, -32767, 32767);
    if ((device.hapticFeatures & SDL_HAPTIC_CONSTANT) != 0 && (device.constantEffect < 0 || moved(constant, device.played.constant))) {
        SDL_HapticEffect effect;
        std::memset(&effect, 0, sizeof(effect));
        effect.type = SDL_HAPTIC_CONSTANT;
        effect.constant.direction.type = SDL_HAPTIC_CARTESIAN;
        effect.constant.direction.dir[0] = 1;
        effect.constant.length = SDL_HAPTIC_INFINITY;
        const int level = scale(settings.ffbInvert ? -constant : constant);
        effect.constant.level = static_cast<Sint16>(std::clamp(level, -32767, 32767));
        const int id = playEffect(device.haptic, device.constantEffect, effect, running);
        if (id < 0 && device.constantEffect < 0) APS5_LOG_OUT("Pad: wheel refused a constant force: %s", SDL_GetError());
        device.constantEffect = id;
        device.played.constant = constant;
    }
    const auto condition = [&](Uint16 type, unsigned feature, int& id, int coefficient, int center, int& playedCoefficient, int* playedCenter, int limit) {
        if ((device.hapticFeatures & feature) == 0) return;
        if (id >= 0 && !moved(coefficient, playedCoefficient) && (playedCenter == nullptr || !moved(center, *playedCenter))) return;
        SDL_HapticEffect effect = conditionEffect(type, device.conditionNeedsDirection, scale(coefficient), center, limit);
        int result = playEffect(device.haptic, id, effect, running);
        if (result < 0 && !device.conditionNeedsDirection) {
            effect = conditionEffect(type, true, scale(coefficient), center, limit);
            result = playEffect(device.haptic, -1, effect, running);
            if (result >= 0) device.conditionNeedsDirection = true;
        }
        id = result;
        playedCoefficient = coefficient;
        if (playedCenter != nullptr) *playedCenter = center;
    };
    condition(SDL_HAPTIC_SPRING, SDL_HAPTIC_SPRING, device.gameSpringEffect, wanted.spring, wanted.springCenter, device.played.spring, &device.played.springCenter, wanted.springLimit);
    condition(SDL_HAPTIC_DAMPER, SDL_HAPTIC_DAMPER, device.damperEffect, wanted.damper, 0, device.played.damper, nullptr, 0xFFFF);
    condition(SDL_HAPTIC_FRICTION, SDL_HAPTIC_FRICTION, device.frictionEffect, wanted.friction, 0, device.played.friction, nullptr, 0xFFFF);
    device.played.active = true;
}
