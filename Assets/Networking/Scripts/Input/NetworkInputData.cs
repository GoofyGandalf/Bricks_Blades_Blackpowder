using Fusion;
using UnityEngine;

// Struct Adopted from Fusion Docs
// https://doc.photonengine.com/fusion/current/tutorials/host-mode-basics/2-setting-up-a-scene
public struct NetworkInputData : INetworkInput
{
    public Vector3 direction;
    public NetworkBool running;
    public NetworkBool attack;
    public NetworkBool combat;
    public NetworkBool block;
    public Vector3 lookDirection;
    public bool strafe;
}