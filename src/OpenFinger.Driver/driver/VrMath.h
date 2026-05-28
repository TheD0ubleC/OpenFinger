#pragma once

#include "openvr_driver.h"

#include <cmath>

namespace openfinger
{

constexpr double kPi = 3.14159265358979323846;

inline double DegToRad(double degrees)
{
    return degrees * kPi / 180.0;
}

inline vr::HmdQuaternion_t IdentityQuaternion()
{
    return { 1.0, 0.0, 0.0, 0.0 };
}

template <typename TMatrix>
inline vr::HmdQuaternion_t HmdQuaternionFromMatrix(const TMatrix& matrix)
{
    vr::HmdQuaternion_t q {};

    q.w = std::sqrt(std::fmax(0.0, 1.0 + matrix.m[0][0] + matrix.m[1][1] + matrix.m[2][2])) / 2.0;
    q.x = std::sqrt(std::fmax(0.0, 1.0 + matrix.m[0][0] - matrix.m[1][1] - matrix.m[2][2])) / 2.0;
    q.y = std::sqrt(std::fmax(0.0, 1.0 - matrix.m[0][0] + matrix.m[1][1] - matrix.m[2][2])) / 2.0;
    q.z = std::sqrt(std::fmax(0.0, 1.0 - matrix.m[0][0] - matrix.m[1][1] + matrix.m[2][2])) / 2.0;

    q.x = std::copysign(q.x, matrix.m[2][1] - matrix.m[1][2]);
    q.y = std::copysign(q.y, matrix.m[0][2] - matrix.m[2][0]);
    q.z = std::copysign(q.z, matrix.m[1][0] - matrix.m[0][1]);

    return q;
}

inline vr::HmdQuaternion_t HmdQuaternionFromSwingTwist(const vr::HmdVector2_t& swing, double twist)
{
    vr::HmdQuaternion_t result {};

    const double swing_squared = (swing.v[0] * swing.v[0]) + (swing.v[1] * swing.v[1]);
    if (swing_squared > 0.0)
    {
        const double theta_swing = std::sqrt(swing_squared);
        const double cos_half_theta_swing = std::cos(theta_swing * 0.5);
        const double cos_half_theta_twist = std::cos(twist * 0.5);
        const double sin_half_theta_twist = std::sin(twist * 0.5);
        const double sin_half_theta_swing_over_theta = std::sin(theta_swing * 0.5) / theta_swing;

        result.w = cos_half_theta_swing * cos_half_theta_twist;
        result.x = cos_half_theta_swing * sin_half_theta_twist;
        result.y = (swing.v[1] * cos_half_theta_twist * sin_half_theta_swing_over_theta)
                 - (swing.v[0] * sin_half_theta_twist * sin_half_theta_swing_over_theta);
        result.z = (swing.v[0] * cos_half_theta_twist * sin_half_theta_swing_over_theta)
                 + (swing.v[1] * sin_half_theta_twist * sin_half_theta_swing_over_theta);
    }
    else
    {
        const double half_twist = twist * 0.5;
        const double cos_half_twist = std::cos(half_twist);
        const double sin_half_twist = std::sin(half_twist);

        result.w = cos_half_twist;
        result.x = sin_half_twist;
        result.y = swing.v[1] * cos_half_twist * 0.5;
        result.z = swing.v[0] * cos_half_twist * 0.5;
    }

    return result;
}

inline vr::HmdQuaternion_t HmdQuaternionFromEulerAngles(double roll, double pitch, double yaw)
{
    const double cr = std::cos(roll * 0.5);
    const double sr = std::sin(roll * 0.5);
    const double cp = std::cos(pitch * 0.5);
    const double sp = std::sin(pitch * 0.5);
    const double cy = std::cos(yaw * 0.5);
    const double sy = std::sin(yaw * 0.5);

    vr::HmdQuaternion_t q {};
    q.w = cr * cp * cy + sr * sp * sy;
    q.x = cr * sp * cy + sr * cp * sy;
    q.y = cr * cp * sy - sr * sp * cy;
    q.z = sr * cp * cy - cr * sp * sy;
    return q;
}

template <typename TInput, typename TOutput>
inline void ConvertQuaternion(const TInput& input, TOutput* output)
{
    output->w = static_cast<float>(input.w);
    output->x = static_cast<float>(input.x);
    output->y = static_cast<float>(input.y);
    output->z = static_cast<float>(input.z);
}

inline vr::HmdQuaternion_t operator-(const vr::HmdQuaternion_t& quaternion)
{
    return { quaternion.w, -quaternion.x, -quaternion.y, -quaternion.z };
}

inline vr::HmdQuaternion_t operator*(const vr::HmdQuaternion_t& lhs, const vr::HmdQuaternion_t& rhs)
{
    return {
        lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z,
        lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
        lhs.w * rhs.y - lhs.x * rhs.z + lhs.y * rhs.w + lhs.z * rhs.x,
        lhs.w * rhs.z + lhs.x * rhs.y - lhs.y * rhs.x + lhs.z * rhs.w,
    };
}

inline vr::HmdVector3_t operator+(const vr::HmdVector3_t& lhs, const vr::HmdVector3_t& rhs)
{
    return { lhs.v[0] + rhs.v[0], lhs.v[1] + rhs.v[1], lhs.v[2] + rhs.v[2] };
}

inline vr::HmdVector3_t operator*(const vr::HmdVector3_t& vector, const vr::HmdQuaternion_t& quaternion)
{
    const vr::HmdQuaternion_t qvec = { 0.0, vector.v[0], vector.v[1], vector.v[2] };
    const vr::HmdQuaternion_t result = (quaternion * qvec) * (-quaternion);
    return {
        static_cast<float>(result.x),
        static_cast<float>(result.y),
        static_cast<float>(result.z),
    };
}

inline vr::HmdVector3_t HmdVector3From34Matrix(const vr::HmdMatrix34_t& matrix)
{
    return { matrix.m[0][3], matrix.m[1][3], matrix.m[2][3] };
}

inline vr::HmdQuaternion_t ToDoubleQuaternion(const vr::HmdQuaternionf_t& q)
{
    return { q.w, q.x, q.y, q.z };
}

inline vr::HmdVector3_t ToVector3(const vr::HmdVector4_t& v)
{
    return { v.v[0], v.v[1], v.v[2] };
}

inline vr::VRBoneTransform_t IdentityBoneTransform()
{
    vr::VRBoneTransform_t transform {};
    transform.position = { 0.0f, 0.0f, 0.0f, 1.0f };
    transform.orientation = { 1.0f, 0.0f, 0.0f, 0.0f };
    return transform;
}

} // namespace openfinger
