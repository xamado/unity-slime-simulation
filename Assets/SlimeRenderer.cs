using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SlimeRenderer : MonoBehaviour
{
    [SerializeField] private SlimeSimulation _simulation;

    [SerializeField] private Material _debugMaterial;

    [SerializeField] private bool _showTrailMap;

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (_showTrailMap)
        {
            Graphics.Blit(_simulation.TrailMap, null as RenderTexture);
        }
        else
        {
            Graphics.Blit(_simulation.Texture, null as RenderTexture);
        }

        if (_simulation.ShowDebug)
        {
            Graphics.Blit(_simulation.DebugLayerTexture, null as RenderTexture, _debugMaterial);
        }
    }
}
