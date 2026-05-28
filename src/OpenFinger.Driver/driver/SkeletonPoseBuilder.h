#pragma once

#include "common/Config.h"
#include "openvr_driver.h"

#include <array>
#include <cstdint>

namespace openfinger
{

enum HandBoneIndex
{
    eBone_Root = 0,
    eBone_Wrist,
    eBone_Thumb0,
    eBone_Thumb1,
    eBone_Thumb2,
    eBone_Thumb3,
    eBone_IndexFinger0,
    eBone_IndexFinger1,
    eBone_IndexFinger2,
    eBone_IndexFinger3,
    eBone_IndexFinger4,
    eBone_MiddleFinger0,
    eBone_MiddleFinger1,
    eBone_MiddleFinger2,
    eBone_MiddleFinger3,
    eBone_MiddleFinger4,
    eBone_RingFinger0,
    eBone_RingFinger1,
    eBone_RingFinger2,
    eBone_RingFinger3,
    eBone_RingFinger4,
    eBone_PinkyFinger0,
    eBone_PinkyFinger1,
    eBone_PinkyFinger2,
    eBone_PinkyFinger3,
    eBone_PinkyFinger4,
    eBone_Aux_Thumb,
    eBone_Aux_IndexFinger,
    eBone_Aux_MiddleFinger,
    eBone_Aux_RingFinger,
    eBone_Aux_PinkyFinger,
    eBone_Count
};

class SkeletonPoseBuilder
{
public:
    static constexpr std::uint32_t kBoneCount = eBone_Count;

    void BuildHand(
        HandSide side,
        const std::array<float, kFingerCount>& bends,
        vr::VRBoneTransform_t* without_controller,
        vr::VRBoneTransform_t* with_controller) const;

private:
    void BuildCommonHand(HandSide side, const std::array<float, kFingerCount>& bends, vr::VRBoneTransform_t* out_transforms) const;
};

} // namespace openfinger
