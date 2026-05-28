#include "driver/SkeletonPoseBuilder.h"

#include "driver/VrMath.h"

#include <algorithm>
#include <array>

namespace openfinger
{

namespace
{

struct HandSimSplayableJoint
{
    vr::HmdVector2_t swing = { 0.0f, 0.0f };
    double twist = 0.0;
};

struct HandSimJoint
{
    double rotation = 0.0;
};

struct HandSimThumb
{
    HandSimSplayableJoint metacarpal;
    HandSimSplayableJoint proximal;
    HandSimJoint distal;
};

struct HandSimFinger
{
    HandSimSplayableJoint metacarpal;
    HandSimSplayableJoint proximal;
    HandSimJoint intermediate;
    HandSimJoint distal;
};

struct HandSimHand
{
    HandSimThumb thumb;
    std::array<HandSimFinger, 4> fingers;
};

struct WorldTransform
{
    vr::HmdQuaternion_t orientation = IdentityQuaternion();
    vr::HmdVector3_t position = { 0.0f, 0.0f, 0.0f };
};

constexpr float kFingerJointLengths[5][5] = {
    { 0.05f, 0.05f, 0.035f, 0.025f, 0.0f },
    { 0.03f, 0.073f, 0.045f, 0.025f, 0.02f },
    { 0.01f, 0.091f, 0.049f, 0.03f, 0.02f },
    { 0.02f, 0.073f, 0.045f, 0.03f, 0.03f },
    { 0.03f, 0.067f, 0.03f, 0.025f, 0.02f },
};

void InitOpenHandPose(HandSimHand* hand)
{
    for (auto& finger : hand->fingers)
    {
        finger.metacarpal.swing.v[1] = 0.0f;
        finger.metacarpal.twist = 0.0;

        finger.proximal.swing.v[1] = static_cast<float>(DegToRad(10.0));
        finger.intermediate.rotation = DegToRad(5.0);
        finger.distal.rotation = DegToRad(5.0);
    }

    hand->thumb.metacarpal.swing.v[0] = static_cast<float>(DegToRad(10.0));
    hand->thumb.metacarpal.swing.v[1] = static_cast<float>(DegToRad(40.0));
    hand->thumb.metacarpal.twist = DegToRad(70.0);
    hand->thumb.proximal.swing.v[0] = 0.0f;
    hand->thumb.proximal.swing.v[1] = 0.0f;
    hand->thumb.proximal.twist = 0.0;
    hand->thumb.distal.rotation = 0.0;

    hand->fingers[0].metacarpal.swing.v[1] = static_cast<float>(DegToRad(13.0));
    hand->fingers[1].metacarpal.swing.v[1] = static_cast<float>(DegToRad(0.0));
    hand->fingers[2].metacarpal.swing.v[1] = static_cast<float>(DegToRad(-15.0));
    hand->fingers[3].metacarpal.swing.v[1] = static_cast<float>(DegToRad(-27.0));

    hand->fingers[0].proximal.swing.v[1] = static_cast<float>(DegToRad(3.0));
    hand->fingers[1].proximal.swing.v[1] = static_cast<float>(DegToRad(0.0));
    hand->fingers[2].proximal.swing.v[1] = static_cast<float>(DegToRad(-1.0));
    hand->fingers[3].proximal.swing.v[1] = static_cast<float>(DegToRad(-2.0));
}

int FingerStartIndex(int finger_index)
{
    return eBone_IndexFinger0 + (finger_index * 5);
}

void ComputeBoneTransform(const vr::HmdQuaternion_t& orientation, const vr::HmdVector3_t& position, vr::VRBoneTransform_t* out_transform)
{
    ConvertQuaternion(orientation, &out_transform->orientation);
    out_transform->position = { position.v[0], position.v[1], position.v[2], 1.0f };
}

void ComputeBoneTransform(const vr::HmdQuaternion_t& orientation, float joint_length, vr::VRBoneTransform_t* out_transform)
{
    ComputeBoneTransform(orientation, { joint_length, 0.0f, 0.0f }, out_transform);
}

void ComputeBoneTransformMetacarpal(
    HandSide side,
    const vr::HmdQuaternion_t& orientation,
    float joint_length,
    vr::VRBoneTransform_t* out_transform)
{
    const vr::HmdVector3_t offset = { joint_length, 0.0f, 0.0f };

    // Source: Valve handskeletonsimulation sample.
    const vr::HmdQuaternion_t magic = { 0.5, 0.5, -0.5, 0.5 };
    vr::HmdQuaternion_t bone_orientation = magic * orientation;
    vr::HmdVector3_t bone_position = offset * bone_orientation;

    if (side == HandSide::Right)
    {
        // Source: Valve handskeletonsimulation sample. The right hand mirrors the left-hand reference pose.
        std::swap(bone_orientation.w, bone_orientation.x);
        std::swap(bone_orientation.y, bone_orientation.z);
        bone_orientation.x *= -1.0;
        bone_orientation.z *= -1.0;
        bone_position.v[0] *= -1.0f;
    }

    ComputeBoneTransform(bone_orientation, bone_position, out_transform);
}

void MirrorForRightHand(vr::VRBoneTransform_t* wrist_transform)
{
    wrist_transform->position.v[0] *= -1.0f;
    wrist_transform->orientation.y *= -1.0f;
    wrist_transform->orientation.z *= -1.0f;
}

WorldTransform ComposeWorldTransform(const WorldTransform& parent, const vr::VRBoneTransform_t& local)
{
    const vr::HmdQuaternion_t local_orientation = ToDoubleQuaternion(local.orientation);
    const vr::HmdVector3_t local_position = ToVector3(local.position);

    WorldTransform world;
    world.orientation = parent.orientation * local_orientation;
    world.position = parent.position + (local_position * parent.orientation);
    return world;
}

void SetAuxBone(const WorldTransform& world, vr::VRBoneTransform_t* out_transform)
{
    ConvertQuaternion(world.orientation, &out_transform->orientation);
    out_transform->position = { world.position.v[0], world.position.v[1], world.position.v[2], 1.0f };
}

void ApplyThumbBend(HandSimHand* hand, float bend)
{
    bend = std::clamp(bend, 0.0f, 1.0f);
    hand->thumb.metacarpal.swing.v[0] += static_cast<float>(DegToRad(bend * 10.0f));
    hand->thumb.proximal.swing.v[0] += static_cast<float>(DegToRad(bend * 40.0f));
    hand->thumb.distal.rotation += DegToRad(bend * 55.0f);
}

void ApplyFingerBend(HandSimFinger* finger, float bend, double proximal_deg, double intermediate_deg, double distal_deg)
{
    bend = std::clamp(bend, 0.0f, 1.0f);
    finger->metacarpal.swing.v[0] += static_cast<float>(DegToRad(bend * 5.0f));
    finger->proximal.swing.v[0] += static_cast<float>(DegToRad(bend * proximal_deg));
    finger->intermediate.rotation += DegToRad(bend * intermediate_deg);
    finger->distal.rotation += DegToRad(bend * distal_deg);
}

} // namespace

void SkeletonPoseBuilder::BuildHand(
    HandSide side,
    const std::array<float, kFingerCount>& bends,
    vr::VRBoneTransform_t* without_controller,
    vr::VRBoneTransform_t* with_controller) const
{
    BuildCommonHand(side, bends, without_controller);
    BuildCommonHand(side, bends, with_controller);
}

void SkeletonPoseBuilder::BuildCommonHand(
    HandSide side,
    const std::array<float, kFingerCount>& bends,
    vr::VRBoneTransform_t* out_transforms) const
{
    if (out_transforms == nullptr)
    {
        return;
    }

    for (int index = 0; index < eBone_Count; ++index)
    {
        out_transforms[index] = IdentityBoneTransform();
    }

    HandSimHand hand {};
    InitOpenHandPose(&hand);

    ApplyThumbBend(&hand, bends[0]);
    ApplyFingerBend(&hand.fingers[0], bends[1], 70.0, 90.0, 60.0);
    ApplyFingerBend(&hand.fingers[1], bends[2], 75.0, 95.0, 65.0);
    ApplyFingerBend(&hand.fingers[2], bends[3], 78.0, 95.0, 65.0);
    ApplyFingerBend(&hand.fingers[3], bends[4], 82.0, 98.0, 70.0);

    out_transforms[eBone_Root] = IdentityBoneTransform();
    out_transforms[eBone_Wrist].position = { -0.034038f, 0.036503f, 0.164722f, 1.0f };
    out_transforms[eBone_Wrist].orientation = { -0.055147f, -0.078608f, -0.920279f, 0.379296f };
    if (side == HandSide::Right)
    {
        MirrorForRightHand(&out_transforms[eBone_Wrist]);
    }

    ComputeBoneTransformMetacarpal(
        side,
        HmdQuaternionFromSwingTwist(hand.thumb.metacarpal.swing, hand.thumb.metacarpal.twist),
        kFingerJointLengths[0][0],
        &out_transforms[eBone_Thumb0]);
    ComputeBoneTransform(
        HmdQuaternionFromSwingTwist(hand.thumb.proximal.swing, hand.thumb.proximal.twist),
        kFingerJointLengths[0][1],
        &out_transforms[eBone_Thumb1]);
    ComputeBoneTransform(
        HmdQuaternionFromEulerAngles(hand.thumb.distal.rotation, 0.0, 0.0),
        kFingerJointLengths[0][2],
        &out_transforms[eBone_Thumb2]);
    ComputeBoneTransform(IdentityQuaternion(), kFingerJointLengths[0][3], &out_transforms[eBone_Thumb3]);

    for (int finger = 0; finger < 4; ++finger)
    {
        const int start = FingerStartIndex(finger);
        ComputeBoneTransformMetacarpal(
            side,
            HmdQuaternionFromSwingTwist(hand.fingers[finger].metacarpal.swing, hand.fingers[finger].metacarpal.twist),
            kFingerJointLengths[finger + 1][0],
            &out_transforms[start + 0]);
        ComputeBoneTransform(
            HmdQuaternionFromSwingTwist(hand.fingers[finger].proximal.swing, hand.fingers[finger].proximal.twist),
            kFingerJointLengths[finger + 1][1],
            &out_transforms[start + 1]);
        ComputeBoneTransform(
            HmdQuaternionFromEulerAngles(hand.fingers[finger].intermediate.rotation, 0.0, 0.0),
            kFingerJointLengths[finger + 1][2],
            &out_transforms[start + 2]);
        ComputeBoneTransform(
            HmdQuaternionFromEulerAngles(hand.fingers[finger].distal.rotation, 0.0, 0.0),
            kFingerJointLengths[finger + 1][3],
            &out_transforms[start + 3]);
        ComputeBoneTransform(IdentityQuaternion(), kFingerJointLengths[finger + 1][4], &out_transforms[start + 4]);
    }

    const WorldTransform root_world {};
    const WorldTransform wrist_world = ComposeWorldTransform(root_world, out_transforms[eBone_Wrist]);

    const WorldTransform thumb0_world = ComposeWorldTransform(wrist_world, out_transforms[eBone_Thumb0]);
    const WorldTransform thumb1_world = ComposeWorldTransform(thumb0_world, out_transforms[eBone_Thumb1]);
    const WorldTransform thumb2_world = ComposeWorldTransform(thumb1_world, out_transforms[eBone_Thumb2]);
    SetAuxBone(thumb2_world, &out_transforms[eBone_Aux_Thumb]);

    for (int finger = 0; finger < 4; ++finger)
    {
        const int start = FingerStartIndex(finger);
        const WorldTransform finger0_world = ComposeWorldTransform(wrist_world, out_transforms[start + 0]);
        const WorldTransform finger1_world = ComposeWorldTransform(finger0_world, out_transforms[start + 1]);
        const WorldTransform finger2_world = ComposeWorldTransform(finger1_world, out_transforms[start + 2]);
        const WorldTransform finger3_world = ComposeWorldTransform(finger2_world, out_transforms[start + 3]);
        SetAuxBone(finger3_world, &out_transforms[eBone_Aux_IndexFinger + finger]);
    }
}

} // namespace openfinger
