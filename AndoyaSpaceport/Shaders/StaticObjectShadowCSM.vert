#version 450

// Depth pass of the static objects of the site into one cascade of the sun shadow map. It is
// ShadowPassNoPushCSM.vert for the instance buffer of StaticObjectModel, which holds only the world matrix
// (64 bytes per instance), while the part instance buffer also holds a highlight flag.

struct CSMCascade
{
    mat4 viewProj;
    float splitNear;
    float splitFar;
    float invDepthRange;
    float texelWorldSize;
};

// Only the cascades are read. They are the first member of CSMUbo in Lighting/CascadedShadows.glsl, so the
// offsets agree with the full block.
layout (set = 0, binding = 0) uniform CsmShadows
{
    CSMCascade cascades[6];
} shadowUbo;

layout (location = 0) in vec3 inPos;

struct InstanceData
{
    mat4 WorldMatrix;
};

layout (set = 1, binding = 0) readonly buffer InstanceStorageBlock
{
    InstanceData Data[];
} InstanceStorage;

layout (push_constant) uniform PushConsts
{
    int cascadeIndex;
} pushConsts;

void main()
{
    mat4 worldMatrix = InstanceStorage.Data[gl_InstanceIndex].WorldMatrix;
    gl_Position = shadowUbo.cascades[pushConsts.cascadeIndex].viewProj * (worldMatrix * vec4(inPos, 1.0));
}
