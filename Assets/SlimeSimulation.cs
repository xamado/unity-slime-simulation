using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using Unity.VisualScripting.FullSerializer;
using UnityEngine;
using Color = UnityEngine.Color;
using Random = UnityEngine.Random;

public class SlimeSimulation : MonoBehaviour
{
    private enum SpawnType
    {
        Point,
        CircleInward,
        CircleOutward,
        UniformGrid,
        RandomSquare,
    }
    private struct CS_Agent
    {
        public Vector2 position;
        public float angle;
        public int speciesIndex;
    }

    private struct CS_Species
    {
        public float moveSpeed;
        public float steerSpeed;
        public float sensorAngle;
        public float sensorDistance;
        public float sensorSize;
        public float4 color;
        public float4 color2;
        public float4 mask;
        public float trailDecayRate;
        public float trailDiffusionRate;
        public float trailWeight;
    }

    [System.Serializable]
    private struct SpeciesConfig
    {
        public SpawnType spawnType;
        public float spawnRadius;

        public int numAgents;
        public Color color;
        public Color color2;

        public float moveSpeed;
        public float steerSpeed;
        public float sensorAngle;
        public float sensorDistance;
        public float sensorSize;
        public float trailDecayRate;
        public float trailDiffusionRate;
        public float trailWeight;

        //public SpeciesConfig()
        //{
        //    spawnType = SpawnType.Point;
        //    spawnRadius = 1;

        //    numAgents = 1;
        //    color = Color.white;

        //    moveSpeed = 30;
        //    steerSpeed = 12;

        //    sensorAngle = 30;
        //    sensorDistance = 10;
        //    sensorSize = 1;

        //    trailDecayRate = 0.5f;
        //    trailDiffusionRate = 3;
        //    trailWeight = 5;
        //}
    }

    public RenderTexture Texture { get { return _coloredMap; } }
    public RenderTexture TrailMap { get { return _trailMap; } }

    public RenderTexture DebugLayerTexture { get { return _debugLayerTexture; } }

    public bool ShowDebug { get { return _showDebugLayer; } }

    [SerializeField] private bool _showDebugLayer = false;

    [SerializeField] private bool _matchScreenResolution = false;
    [SerializeField] private int _textureWidth = 64;
    [SerializeField] private int _textureHeight = 64;

    [SerializeField] private ComputeShader _simulationShader;

    [SerializeField] private int _simulationStepsPerFrame = 1;
    [SerializeField] private float _deltaTimeStep = 0.1f;

    [SerializeField] private SpeciesConfig[] _speciesConfig;

    private RenderTexture _trailMap;
    private RenderTexture _coloredMap;
    private RenderTexture _debugLayerTexture;

    private ComputeBuffer _agentsBuffer;
    private ComputeBuffer _speciesBuffer;

    private int _debugMaterial;

    private void Start()
    {
        if (_matchScreenResolution)
        {
            _textureWidth = Screen.width;
            _textureHeight = Screen.height;
        }

        _trailMap = new RenderTexture(_textureWidth, _textureHeight, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, 0)
        {
            enableRandomWrite = true
        };
        _trailMap.Create();

        _coloredMap = new RenderTexture(_textureWidth, _textureHeight, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, 0)
        {
            enableRandomWrite = true
        };
        _coloredMap.Create();

        _debugLayerTexture = new RenderTexture(_textureWidth, _textureHeight, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, 0)
        {
            enableRandomWrite = true
        };
        _debugLayerTexture.Create();

        int numAgents = 0;
        int numSpecies = 0;
        foreach(SpeciesConfig config in _speciesConfig)
        {
            numSpecies++;
            numAgents += config.numAgents;
        }

        // pre-allocate these lists
        List<CS_Agent> agents = new List<CS_Agent>(numAgents);
        List<CS_Species> species = new List<CS_Species>(numSpecies);        

        // Initialize species buffer
        for (int s = 0; s < _speciesConfig.Length; s++)
        {
            SpeciesConfig config = _speciesConfig[s];

            Vector4 mask = new Vector4(0,0,0,0);
            mask[s] = 1.0f;

            // Copy the species settings into our CS_Species struct
            species.Add(new CS_Species
            {
                steerSpeed = config.steerSpeed,
                moveSpeed = config.moveSpeed,
                sensorDistance = config.sensorDistance,
                sensorAngle = config.sensorAngle,
                sensorSize = config.sensorSize,
                color = new Vector4(config.color.r, config.color.g, config.color.b, config.color.a),
                color2 = new Vector4(config.color2.r, config.color2.g, config.color2.b, config.color2.a),
                mask = mask,
                trailDecayRate = config.trailDecayRate,
                trailDiffusionRate = config.trailDiffusionRate,
                trailWeight = config.trailWeight
            });

            // Initialize agents for this species
            Vector2 center = new Vector2(_textureWidth * 0.5f, _textureHeight * 0.5f);

            for (int i = 0; i < config.numAgents; i++)
            {
                Vector2 spawnPos = new Vector2(0, 0);
                float spawnAngle = 0.0f;

                switch(config.spawnType)
                {
                    case SpawnType.Point:
                        {
                            spawnPos = center;
                            spawnAngle = UnityEngine.Random.Range(0.0f, 3.1415f * 2.0f);
                        }
                        break;
                    case SpawnType.CircleInward:
                        {
                            spawnPos = center + (_textureHeight * 0.5f * config.spawnRadius * UnityEngine.Random.insideUnitCircle);

                            Vector2 n = (center - spawnPos).normalized;
                            spawnAngle = Mathf.Atan2(n.y, n.x);
                        }
                        break;
                    case SpawnType.CircleOutward:
                        {
                            spawnPos = center + (_textureHeight * 0.5f * config.spawnRadius * UnityEngine.Random.insideUnitCircle);

                            Vector2 n = (center - spawnPos).normalized;
                            spawnAngle = -Mathf.Atan2(n.y, n.x);
                        }
                        break;

                    case SpawnType.UniformGrid:
                        {
                            float numAgentsPerDimension = Mathf.Sqrt(numAgents);
                            float stepX = _textureWidth / numAgentsPerDimension;
                            float stepY = _textureHeight / numAgentsPerDimension;

                            float col = i % numAgentsPerDimension;
                            float row = i / numAgentsPerDimension;                            

                            spawnPos = new Vector2(col * stepX, row * stepY);

                            spawnAngle = 0.0f;
                        }
                        break;

                    case SpawnType.RandomSquare:
                        {
                            spawnPos = new Vector2(Random.Range(0, _textureWidth), Random.Range(0, _textureHeight));
                            spawnAngle = Random.Range(0.0f, 3.1415f * 2.0f);
                        }
                        break;
                };

                agents.Add(new CS_Agent
                {
                    position = spawnPos,
                    angle = spawnAngle,
                    speciesIndex = s,
                });
            }
        }

        // Initialize Species buffer
        _speciesBuffer = new ComputeBuffer(species.Count, Marshal.SizeOf(typeof(CS_Species)), ComputeBufferType.Default);
        _speciesBuffer.SetData(species);

        // Initialize Agents buffer
        _agentsBuffer = new ComputeBuffer(agents.Count, Marshal.SizeOf(typeof(CS_Agent)), ComputeBufferType.Default);
        _agentsBuffer.SetData(agents); 
    }

    private void OnDestroy()
    {
        if (_debugLayerTexture)
        {
            _debugLayerTexture.Release();
        }

        _agentsBuffer.Dispose();
        _speciesBuffer.Dispose(); 
    }

    private void FixedUpdate()
    {
        if (_showDebugLayer)
        {
            if (_debugLayerTexture != null)
            {
                _debugLayerTexture.Release();
                _debugLayerTexture = null;
            }

            _debugLayerTexture = new RenderTexture(_textureWidth, _textureHeight, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, 0)
            {
                enableRandomWrite = true
            };
            _debugLayerTexture.Create();
        }

        // Update agents
        _simulationShader.SetBool("ShowDebug", _showDebugLayer);

        _simulationShader.SetInt("NumSpecies", _speciesBuffer.count);
        _simulationShader.SetInt("NumAgents", _agentsBuffer.count); 
        _simulationShader.SetInt("ResultWidth", _trailMap.width);
        _simulationShader.SetInt("ResultHeight", _trailMap.height);

        _simulationShader.SetFloat("DeltaTime", _deltaTimeStep);
        _simulationShader.SetFloat("Time", Time.time);

        // Simulate
        _simulationShader.SetBuffer(0, "SpeciesBuffer", _speciesBuffer);
        _simulationShader.SetTexture(0, "TrailMap", _trailMap);
        _simulationShader.SetTexture(0, "DebugLayer", _debugLayerTexture);
        _simulationShader.SetBuffer(0, "Agents", _agentsBuffer);

        _simulationShader.SetBuffer(1, "SpeciesBuffer", _speciesBuffer);
        _simulationShader.SetTexture(1, "TrailMap", _trailMap);

        for (int i = 0; i < _simulationStepsPerFrame; i++)
        {
            // Simulate
            _simulationShader.Dispatch(0, _agentsBuffer.count / 16 + 1, 1, 1);

            // Decay
            _simulationShader.Dispatch(1, _trailMap.width / 8 + 1, _trailMap.height / 8 + 1, 1);
        }

        // Update color map
        _simulationShader.SetBuffer(2, "SpeciesBuffer", _speciesBuffer);
        _simulationShader.SetTexture(2, "ColorMap", _coloredMap);
        _simulationShader.SetTexture(2, "TrailMap", _trailMap);
        _simulationShader.Dispatch(2, _coloredMap.width / 8 + 1, _coloredMap.height / 8 + 1, 1);
    }
}
 