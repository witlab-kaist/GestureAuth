using UnityEngine;

public class CollisionDetection : MonoBehaviour
{
    private uint _collidingObjects;

    public uint CollidingObjects
    {
        get { return _collidingObjects; }
    }

    private void OnTriggerEnter(Collider other)
    {
        _collidingObjects++;
    }

    private void OnTriggerExit(Collider other)
    {
        _collidingObjects--;
    }
}