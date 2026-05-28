#pragma once

#include "common/AdcReceiver.h"
#include "common/Config.h"
#include "common/RuntimeState.h"

#include <array>
#include <chrono>
#include <deque>
#include <string_view>

namespace openfinger
{

std::string_view DirectionLabel(BendDirection direction);

class FingerFilter
{
public:
    FingerFilter();
    explicit FingerFilter(const AppConfig& config);

    void SetConfig(const AppConfig& config);
    const AppConfig& config() const;

    const HandRuntimeState& hand_state(HandSide side) const;
    RuntimeFrame BuildRuntimeFrame(std::uint64_t seq, std::uint64_t monotonic_ms) const;

    bool ProcessPacket(HandSide side, const ReceivedAdcPacket& packet);
    void Tick(std::chrono::steady_clock::time_point now);

    bool CalibrateCenterFromCurrentRaw(HandSide side, FingerName finger);
    bool ResetCenter(HandSide side, FingerName finger);
    bool CycleDirection(HandSide side, FingerName finger);
    bool UpdateFingerConfig(HandSide side, FingerName finger, const FingerConfig& config);

private:
    struct FingerTracker
    {
        FingerRuntimeState state;
        bool ema_initialized = false;
        bool has_last_raw = false;
        int last_raw = 0;
        int auto_direction_sign = 0;
        int auto_direction_streak = 0;
    };

    struct HandTracker
    {
        HandRuntimeState state;
        std::array<FingerTracker, kFingerCount> fingers {};
        std::deque<std::chrono::steady_clock::time_point> packet_times;
    };

    void UpdateFpsWindow(HandTracker* hand, std::chrono::steady_clock::time_point now);
    void RefreshFingerStaleState(
        HandSide side,
        FingerName finger_name,
        FingerTracker* tracker,
        HandTracker* hand,
        std::chrono::steady_clock::time_point now);
    void RecomputeCurrentSample(HandSide side, FingerName finger_name, FingerTracker* tracker);

    double ComputeBendFromRaw(const FingerConfig& config, int adc_max, int raw, BendDirection direction) const;
    BendDirection ResolveEffectiveDirection(
        HandSide side,
        FingerName finger_name,
        FingerTracker* finger,
        const FingerConfig& config,
        bool* out_config_changed);

    AppConfig config_;
    HandTracker left_;
    HandTracker right_;
};

} // namespace openfinger
