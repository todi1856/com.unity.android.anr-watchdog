using UnityEngine;

public class Entry : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        Unity.Android.AnrWatchdog.Start();
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
