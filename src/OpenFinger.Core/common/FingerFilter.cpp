#include "common/FingerFilter.h"

#include <algorithm>
#include <cmath>

namespace openfinger
{

namespace
{

constexpr int kAutoDirectionThresholdRaw = 80;
constexpr int kAutoDirectionFrames = 8;
constexpr std::chrono::milliseconds kStaleAfter(500);
constexpr std::chrono::milliseconds kReturnToZeroAfter(3000);

double Clamp01(double value)
{
    return std::clamp(value, 0.0, 1.0);
}

FingerConfig DefaultFinger(std::size_t adc_channel)
{
    FingerConfig config;
    config.adc_channel = static_cast<int>(adc_channel);
    return config;
}

} // namespace

std::string_view DirectionLabel(BendDirection direction)
{
    return ToString(direction);
}

FingerFilter::FingerFilter()
    : FingerFilter(AppConfig {})
{
}

FingerFilter::FingerFilter(const AppConfig& config)
{
    SetConfig(config);
}

void FingerFilter::SetConfig(const AppConfig& config)
{
    config_ = config;
    left_ = {};
    right_ = {};
    left_.state.side = HandSide::Left;
    right_.state.side = HandSide::Right;

    for (std::size_t index = 0; index < kFingerCount; ++index)
    {
        left_.fingers[index].state.center_raw = config_.hands.left.fingers[index].center_raw;
        left_.fingers[index].state.direction = config_.hands.left.fingers[index].direction;
        left_.fingers[index].state.enabled = config_.hands.left.fingers[index].enabled;

        right_.fingers[index].state.center_raw = config_.hands.right.fingers[index].center_raw;
        right_.fingers[index].state.direction = config_.hands.right.fingers[index].direction;
        right_.fingers[index].state.enabled = config_.hands.right.fingers[index].enabled;
    }
}

const AppConfig& FingerFilter::config() const
{
    return config_;
}

const HandRuntimeState& FingerFilter::hand_state(HandSide side) const
{
    return side == HandSide::Left ? left_.state : right_.state;
}

RuntimeFrame FingerFilter::BuildRuntimeFrame(std::uint64_t seq, std::uint64_t monotonic_ms) const
{
    RuntimeFrame frame;
    frame.seq = seq;
    frame.monotonic_ms = monotonic_ms;
    frame.left = left_.state;
    frame.right = right_.state;
    return frame;
}

bool FingerFilter::ProcessPacket(HandSide side, const ReceivedAdcPacket& packet)
{
    HandTracker* hand = (side == HandSide::Left) ? &left_ : &right_;
    HandConfig& hand_config = GetHandConfig(config_, side);

    UpdateFpsWindow(hand, packet.received_at);
    hand->state.side = side;
    hand->state.present = packet.packet.tracking_enabled;
    hand->state.connected = packet.packet.tracking_enabled;
    hand->state.stale = false;
    hand->state.device_ms = packet.packet.device_ms;
    hand->state.last_packet_at = packet.received_at;
    hand->state.source_ip = packet.source_endpoint;
    hand->state.packet_fps = 0.0;

    if (!hand->packet_times.empty())
    {
        if (hand->packet_times.size() == 1)
        {
            hand->state.packet_fps = 1.0;
        }
        else
        {
            const std::chrono::duration<double> span = hand->packet_times.back() - hand->packet_times.front();
            if (span.count() > 0.0)
            {
                hand->state.packet_fps = static_cast<double>(hand->packet_times.size() - 1) / span.count();
            }
        }
    }

    bool config_changed = false;
    for (std::size_t index = 0; index < kFingerCount; ++index)
    {
        FingerTracker& finger = hand->fingers[index];
        FingerRuntimeState& state = finger.state;
        FingerConfig& finger_config = hand_config.fingers[index];

        state.enabled = finger_config.enabled;
        state.center_raw = finger_config.center_raw;
        state.direction = finger_config.direction;

        if (!finger_config.enabled)
        {
            RefreshFingerStaleState(side, FingerNameFromIndex(index), &finger, hand, packet.received_at);
            continue;
        }

        const int channel = std::clamp(finger_config.adc_channel, 0, static_cast<int>(kFingerCount - 1));
        if ((packet.packet.mask & (1 << channel)) == 0)
        {
            RefreshFingerStaleState(side, FingerNameFromIndex(index), &finger, hand, packet.received_at);
            continue;
        }

        state.has_valid_sample = true;
        state.stale = false;
        hand->state.last_valid_at = packet.received_at;
        state.raw = std::clamp(packet.packet.raw[channel], 0, config_.adc_max);

        finger.last_raw = state.raw;
        finger.has_last_raw = true;

        bool finger_config_changed = false;
        const BendDirection effective_direction =
            ResolveEffectiveDirection(side, FingerNameFromIndex(index), &finger, finger_config, &finger_config_changed);
        state.direction = finger_config.direction;
        state.center_raw = finger_config.center_raw;
        state.bend_raw = ComputeBendFromRaw(finger_config, config_.adc_max, state.raw, effective_direction);

        const double alpha = std::clamp(finger_config.smoothing_alpha, 0.0, 1.0);
        if (!finger.ema_initialized)
        {
            state.bend_smoothed = state.bend_raw;
            finger.ema_initialized = true;
        }
        else
        {
            state.bend_smoothed = (state.bend_smoothed * (1.0 - alpha)) + (state.bend_raw * alpha);
        }

        if (state.bend_smoothed < finger_config.deadzone)
        {
            state.bend_smoothed = 0.0;
        }

        config_changed = config_changed || finger_config_changed;
    }

    return config_changed;
}

void FingerFilter::Tick(std::chrono::steady_clock::time_point now)
{
    for (std::size_t index = 0; index < kFingerCount; ++index)
    {
        RefreshFingerStaleState(HandSide::Left, FingerNameFromIndex(index), &left_.fingers[index], &left_, now);
        RefreshFingerStaleState(HandSide::Right, FingerNameFromIndex(index), &right_.fingers[index], &right_, now);
    }

    left_.state.stale = true;
    right_.state.stale = true;
    for (const auto& finger : left_.fingers)
    {
        if (finger.state.has_valid_sample && !finger.state.stale)
        {
            left_.state.stale = false;
            break;
        }
    }
    for (const auto& finger : right_.fingers)
    {
        if (finger.state.has_valid_sample && !finger.state.stale)
        {
            right_.state.stale = false;
            break;
        }
    }
}

bool FingerFilter::CalibrateCenterFromCurrentRaw(HandSide side, FingerName finger_name)
{
    FingerTracker& finger = (side == HandSide::Left ? left_ : right_).fingers[FingerIndex(finger_name)];
    if (!finger.has_last_raw)
    {
        return false;
    }

    FingerConfig& config = GetFingerConfig(config_, side, finger_name);
    config.center_raw = std::clamp(finger.last_raw, 0, config_.adc_max);
    finger.state.center_raw = config.center_raw;
    if (config.direction == BendDirection::Auto)
    {
        finger.auto_direction_sign = 0;
        finger.auto_direction_streak = 0;
    }
    finger.ema_initialized = false;
    RecomputeCurrentSample(side, finger_name, &finger);
    return true;
}

bool FingerFilter::ResetCenter(HandSide side, FingerName finger_name)
{
    FingerTracker& finger = (side == HandSide::Left ? left_ : right_).fingers[FingerIndex(finger_name)];
    FingerConfig& config = GetFingerConfig(config_, side, finger_name);
    config.center_raw = std::clamp(2048, 0, config_.adc_max);
    finger.state.center_raw = config.center_raw;
    if (config.direction == BendDirection::Auto)
    {
        finger.auto_direction_sign = 0;
        finger.auto_direction_streak = 0;
    }
    finger.ema_initialized = false;
    RecomputeCurrentSample(side, finger_name, &finger);
    return true;
}

bool FingerFilter::CycleDirection(HandSide side, FingerName finger_name)
{
    FingerTracker& finger = (side == HandSide::Left ? left_ : right_).fingers[FingerIndex(finger_name)];
    FingerConfig& config = GetFingerConfig(config_, side, finger_name);
    switch (config.direction)
    {
    case BendDirection::Auto:
        config.direction = BendDirection::Positive;
        break;
    case BendDirection::Positive:
        config.direction = BendDirection::Negative;
        break;
    case BendDirection::Negative:
        config.direction = BendDirection::Absolute;
        break;
    case BendDirection::Absolute:
        config.direction = BendDirection::Auto;
        break;
    }

    finger.state.direction = config.direction;
    finger.auto_direction_sign = 0;
    finger.auto_direction_streak = 0;
    finger.ema_initialized = false;
    RecomputeCurrentSample(side, finger_name, &finger);
    return true;
}

bool FingerFilter::UpdateFingerConfig(HandSide side, FingerName finger_name, const FingerConfig& config)
{
    FingerTracker& finger = (side == HandSide::Left ? left_ : right_).fingers[FingerIndex(finger_name)];
    FingerConfig updated = config;
    updated.adc_channel = std::clamp(updated.adc_channel, 0, static_cast<int>(kFingerCount - 1));
    updated.center_raw = std::clamp(updated.center_raw, 0, config_.adc_max);
    updated.deadzone = std::clamp(updated.deadzone, 0.0, 1.0);
    updated.smoothing_alpha = std::clamp(updated.smoothing_alpha, 0.0, 1.0);

    FingerConfig& current = GetFingerConfig(config_, side, finger_name);
    const bool channel_changed = current.adc_channel != updated.adc_channel;
    const bool direction_changed = current.direction != updated.direction;
    current = updated;

    finger.state.enabled = current.enabled;
    finger.state.center_raw = current.center_raw;
    finger.state.direction = current.direction;

    if (direction_changed || current.direction == BendDirection::Auto)
    {
        finger.auto_direction_sign = 0;
        finger.auto_direction_streak = 0;
    }

    if (channel_changed)
    {
        finger.has_last_raw = false;
        finger.ema_initialized = false;
        finger.state.has_valid_sample = false;
        finger.state.raw = 0;
        finger.state.bend_raw = 0.0;
        finger.state.bend_smoothed = 0.0;
        finger.state.stale = true;
        return true;
    }

    if (!current.enabled)
    {
        finger.ema_initialized = false;
        finger.state.bend_raw = 0.0;
        finger.state.bend_smoothed = 0.0;
        finger.state.stale = true;
        return true;
    }

    finger.ema_initialized = false;
    RecomputeCurrentSample(side, finger_name, &finger);
    return true;
}

void FingerFilter::UpdateFpsWindow(HandTracker* hand, std::chrono::steady_clock::time_point now)
{
    if (hand == nullptr)
    {
        return;
    }

    hand->packet_times.push_back(now);
    while (!hand->packet_times.empty() && (now - hand->packet_times.front()) > std::chrono::seconds(1))
    {
        hand->packet_times.pop_front();
    }
}

void FingerFilter::RefreshFingerStaleState(
    HandSide side,
    FingerName finger_name,
    FingerTracker* tracker,
    HandTracker* hand,
    std::chrono::steady_clock::time_point now)
{
    if (tracker == nullptr || hand == nullptr)
    {
        return;
    }

    const FingerConfig& config = GetFingerConfig(config_, side, finger_name);
    tracker->state.enabled = config.enabled;
    tracker->state.center_raw = config.center_raw;
    tracker->state.direction = config.direction;

    if (!tracker->state.has_valid_sample)
    {
        tracker->state.stale = true;
        tracker->state.bend_raw = 0.0;
        tracker->state.bend_smoothed = 0.0;
        return;
    }

    const auto time_since_valid = now - hand->state.last_valid_at;
    tracker->state.stale = time_since_valid > kStaleAfter;

    if (time_since_valid > kReturnToZeroAfter && config_.steamvr.stale_return_to_zero)
    {
        const double alpha = std::clamp(config.smoothing_alpha, 0.0, 1.0);
        tracker->state.bend_raw = 0.0;
        tracker->state.bend_smoothed = tracker->state.bend_smoothed * (1.0 - alpha);
        if (tracker->state.bend_smoothed < config.deadzone)
        {
            tracker->state.bend_smoothed = 0.0;
        }
    }
}

void FingerFilter::RecomputeCurrentSample(HandSide side, FingerName finger_name, FingerTracker* tracker)
{
    if (tracker == nullptr)
    {
        return;
    }

    const FingerConfig& config = GetFingerConfig(config_, side, finger_name);
    if (!tracker->has_last_raw)
    {
        tracker->state.bend_raw = 0.0;
        tracker->state.bend_smoothed = 0.0;
        return;
    }

    bool ignored = false;
    const BendDirection effective_direction = ResolveEffectiveDirection(side, finger_name, tracker, config, &ignored);
    tracker->state.direction = config.direction;
    tracker->state.center_raw = config.center_raw;
    tracker->state.bend_raw = ComputeBendFromRaw(config, config_.adc_max, tracker->last_raw, effective_direction);
    tracker->state.bend_smoothed = tracker->state.bend_raw;
    if (tracker->state.bend_smoothed < config.deadzone)
    {
        tracker->state.bend_smoothed = 0.0;
    }
    tracker->ema_initialized = true;
}

double FingerFilter::ComputeBendFromRaw(const FingerConfig& config, int adc_max, int raw, BendDirection direction) const
{
    if (config.calibrated_open_raw >= 0
        && config.calibrated_closed_raw >= 0
        && config.calibrated_open_raw != config.calibrated_closed_raw)
    {
        const double open = static_cast<double>(std::clamp(config.calibrated_open_raw, 0, adc_max));
        const double closed = static_cast<double>(std::clamp(config.calibrated_closed_raw, 0, adc_max));
        const double denom = closed - open;
        if (std::abs(denom) > 0.5)
        {
            double bend = (static_cast<double>(raw) - open) / denom;
            bend = Clamp01(bend);
            if (bend < config.deadzone)
            {
                bend = 0.0;
            }
            return bend;
        }
    }

    const int center = std::clamp(config.center_raw, 0, adc_max);
    const int max_raw = std::max(adc_max, 1);

    double bend = 0.0;
    switch (direction)
    {
    case BendDirection::Positive:
        bend = static_cast<double>(raw - center) / static_cast<double>(std::max(1, max_raw - center));
        break;
    case BendDirection::Negative:
        bend = static_cast<double>(center - raw) / static_cast<double>(std::max(1, center));
        break;
    case BendDirection::Absolute:
    case BendDirection::Auto:
        bend = static_cast<double>(std::abs(raw - center)) / static_cast<double>(std::max(center, max_raw - center));
        break;
    }

    bend = Clamp01(bend);
    if (bend < config.deadzone)
    {
        bend = 0.0;
    }
    return bend;
}

BendDirection FingerFilter::ResolveEffectiveDirection(
    HandSide side,
    FingerName finger_name,
    FingerTracker* finger,
    const FingerConfig& config,
    bool* out_config_changed)
{
    if (out_config_changed != nullptr)
    {
        *out_config_changed = false;
    }

    if (config.direction != BendDirection::Auto)
    {
        return config.direction;
    }

    const int delta = finger->last_raw - config.center_raw;
    if (std::abs(delta) <= kAutoDirectionThresholdRaw)
    {
        finger->auto_direction_sign = 0;
        finger->auto_direction_streak = 0;
        return BendDirection::Absolute;
    }

    const int sign = delta > 0 ? 1 : -1;
    if (sign == finger->auto_direction_sign)
    {
        ++finger->auto_direction_streak;
    }
    else
    {
        finger->auto_direction_sign = sign;
        finger->auto_direction_streak = 1;
    }

    if (finger->auto_direction_streak >= kAutoDirectionFrames)
    {
        FingerConfig& mutable_config = GetFingerConfig(config_, side, finger_name);
        mutable_config.direction = sign > 0 ? BendDirection::Positive : BendDirection::Negative;
        finger->state.direction = mutable_config.direction;
        if (out_config_changed != nullptr)
        {
            *out_config_changed = true;
        }
        return mutable_config.direction;
    }

    return BendDirection::Absolute;
}

} // namespace openfinger
