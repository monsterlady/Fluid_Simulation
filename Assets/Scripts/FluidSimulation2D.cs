using UnityEngine;
using Unity.Mathematics;

public class FluidSimulation2D : MonoBehaviour
{
    public event System.Action OnSimulationStepComplete;

    [Header("Simulation Parameters")] public float simulationSpeed = 1;
    public bool useFixedTimeSteps;
    public int stepsPerFrame;
    public float simulationGravity;
    [Range(0, 1)] public float restitutionCoefficient = 0.95f;
    public float interactionRadius = 2;
    public float fluidDensityTarget;
    public float pressureFactor;
    public float nearbyPressureFactor;
    public float viscosityCoefficient;
    public Vector2 simulationBounds;
    public Vector2 obstacleDimensions;
    public Vector2 obstaclePosition;

    [Header("Interaction Parameters")] public float userInteractionRadius;
    public float userForceMagnitude;

    [Header("Shader References")] public ComputeShader simulationComputeShader;
    public ParticleSpawner particleGenerator;
    public ParticleRenderer particleRenderer;

    // GPU Buffers
    public ComputeBuffer particlePositionsBuffer { get; private set; }
    public ComputeBuffer particleVelocitiesBuffer { get; private set; }
    public ComputeBuffer fluidDensityBuffer { get; private set; }
    ComputeBuffer predictedPositionsBuffer;
    ComputeBuffer hashGridIndices;
    ComputeBuffer hashGridOffsets;
    GPUSort sortingAlgorithm;

    // Compute Shader Kernel Indices
    const int applyExternalForcesKernel = 0;
    const int computeSpatialHashKernel = 1;
    const int computeDensityKernel = 2;
    const int computePressureKernel = 3;
    const int computeViscosityKernel = 4;
    const int updateParticlePositionsKernel = 5;

    // Simulation State
    bool simulationPaused;
    ParticleSpawner.ParticleSpawnData initialSpawnData;
    bool pauseAfterNextFrame;

    public int totalParticles { get; private set; }

    // 在游戏开始时初始化
    void Start()
    {
        Debug.Log("开始初始化");

        float frameDeltaTime = 1 / 60f;
        Time.fixedDeltaTime = frameDeltaTime;

        initialSpawnData = particleGenerator.GetSpawnData();
        totalParticles = initialSpawnData.positions.Length;

        // 初始化GPU缓存
        particlePositionsBuffer = ComputeHelper.CreateStructuredBuffer<float2>(totalParticles);
        predictedPositionsBuffer = ComputeHelper.CreateStructuredBuffer<float2>(totalParticles);
        particleVelocitiesBuffer = ComputeHelper.CreateStructuredBuffer<float2>(totalParticles);
        fluidDensityBuffer = ComputeHelper.CreateStructuredBuffer<float2>(totalParticles);
        hashGridIndices = ComputeHelper.CreateStructuredBuffer<uint3>(totalParticles);
        hashGridOffsets = ComputeHelper.CreateStructuredBuffer<uint>(totalParticles);

        // 设置缓存数据
        InitializeBufferWithData(initialSpawnData);

        // 设置计算着色器
        ComputeHelper.SetBuffer(simulationComputeShader, particlePositionsBuffer, "Positions",
            applyExternalForcesKernel, updateParticlePositionsKernel);
        ComputeHelper.SetBuffer(simulationComputeShader, predictedPositionsBuffer, "PredictedPositions",
            applyExternalForcesKernel, computeSpatialHashKernel, computeDensityKernel, computePressureKernel,
            computeViscosityKernel);
        ComputeHelper.SetBuffer(simulationComputeShader, hashGridIndices, "SpatialIndices", computeSpatialHashKernel,
            computeDensityKernel, computePressureKernel, computeViscosityKernel);
        ComputeHelper.SetBuffer(simulationComputeShader, hashGridOffsets, "SpatialOffsets", computeSpatialHashKernel,
            computeDensityKernel, computePressureKernel, computeViscosityKernel);
        ComputeHelper.SetBuffer(simulationComputeShader, fluidDensityBuffer, "Densities", computeDensityKernel,
            computePressureKernel, computeViscosityKernel);
        ComputeHelper.SetBuffer(simulationComputeShader, particleVelocitiesBuffer, "Velocities",
            applyExternalForcesKernel, computePressureKernel, computeViscosityKernel, updateParticlePositionsKernel);

        simulationComputeShader.SetInt("numParticles", totalParticles);

        sortingAlgorithm = new();
        sortingAlgorithm.SetBuffers(hashGridIndices, hashGridOffsets);

        // 初始化粒子显示
        particleRenderer.Init(this);
    }

    // 在每个固定的物理更新帧调用
    void FixedUpdate()
    {
        if (useFixedTimeSteps)
        {
            ExecuteSimulationFrame(Time.fixedDeltaTime);
        }
    }

    // 在每个更新帧调用
    void Update()
    {
        if (!useFixedTimeSteps && Time.frameCount > 10)
        {
            ExecuteSimulationFrame(Time.deltaTime);
        }

        if (pauseAfterNextFrame)
        {
            simulationPaused = true;
            pauseAfterNextFrame = false;
        }

        ProcessUserInput();
    }

    // 在对象销毁时释放资源
    void OnDestroy()
    {
        ComputeHelper.Release(particlePositionsBuffer, predictedPositionsBuffer, particleVelocitiesBuffer,
            fluidDensityBuffer, hashGridIndices, hashGridOffsets);
    }

    // 在场景中绘制辅助图形
    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0, 1, 0, 0.4f);
        Gizmos.DrawWireCube(Vector2.zero, simulationBounds);
        Gizmos.DrawWireCube(obstaclePosition, obstacleDimensions);
    } // 执行单个模拟步骤

    void PerformSimulationStep()
    {
        ComputeHelper.Dispatch(simulationComputeShader, totalParticles, kernelIndex: applyExternalForcesKernel);
        ComputeHelper.Dispatch(simulationComputeShader, totalParticles, kernelIndex: computeSpatialHashKernel);
        sortingAlgorithm.SortAndCalculateOffsets();
        ComputeHelper.Dispatch(simulationComputeShader, totalParticles, kernelIndex: computeDensityKernel);
        ComputeHelper.Dispatch(simulationComputeShader, totalParticles, kernelIndex: computePressureKernel);
        ComputeHelper.Dispatch(simulationComputeShader, totalParticles, kernelIndex: computeViscosityKernel);
        ComputeHelper.Dispatch(simulationComputeShader, totalParticles, kernelIndex: updateParticlePositionsKernel);
    }

    // 执行模拟帧，分解为多个模拟步骤
    void ExecuteSimulationFrame(float frameDuration)
    {
        if (!simulationPaused)
        {
            float stepDuration = frameDuration / stepsPerFrame * simulationSpeed;

            ApplySimulationSettings(stepDuration);

            for (int i = 0; i < stepsPerFrame; i++)
            {
                PerformSimulationStep();
                OnSimulationStepComplete?.Invoke();
            }
        }
    }


    // 应用模拟设置
    void ApplySimulationSettings(float deltaTime)
    {
        simulationComputeShader.SetFloat("deltaTime", deltaTime);
        simulationComputeShader.SetFloat("gravity", simulationGravity);
        simulationComputeShader.SetFloat("collisionDamping", restitutionCoefficient);
        simulationComputeShader.SetFloat("smoothingRadius", interactionRadius);
        simulationComputeShader.SetFloat("targetDensity", fluidDensityTarget);
        simulationComputeShader.SetFloat("pressureMultiplier", pressureFactor);
        simulationComputeShader.SetFloat("nearPressureMultiplier", nearbyPressureFactor);
        simulationComputeShader.SetFloat("viscosityStrength", viscosityCoefficient);
        simulationComputeShader.SetVector("boundsSize", simulationBounds);
        simulationComputeShader.SetVector("obstacleSize", obstacleDimensions);
        simulationComputeShader.SetVector("obstacleCentre", obstaclePosition);

        simulationComputeShader.SetFloat("Poly6ScalingFactor", 4 / (Mathf.PI * Mathf.Pow(interactionRadius, 8)));
        simulationComputeShader.SetFloat("SpikyPow3ScalingFactor", 10 / (Mathf.PI * Mathf.Pow(interactionRadius, 5)));
        simulationComputeShader.SetFloat("SpikyPow2ScalingFactor", 6 / (Mathf.PI * Mathf.Pow(interactionRadius, 4)));
        simulationComputeShader.SetFloat("SpikyPow3DerivativeScalingFactor",
            30 / (Mathf.Pow(interactionRadius, 5) * Mathf.PI));
        simulationComputeShader.SetFloat("SpikyPow2DerivativeScalingFactor",
            12 / (Mathf.Pow(interactionRadius, 4) * Mathf.PI));
        // 根据鼠标输入计算交互力
        Vector2 currentMousePosition = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        // #bool attracting = Input.GetMouseButton(0);
        bool impluse = Input.GetMouseButton(0);
        float interactionStrength = 0;
        if (impluse)
        {
            Debug.Log("鼠标左键按下: " + currentMousePosition + " " + userForceMagnitude);
            interactionStrength = -userForceMagnitude;
        }

        simulationComputeShader.SetVector("interactionInputPoint", currentMousePosition);
        simulationComputeShader.SetFloat("interactionInputStrength", interactionStrength);
        simulationComputeShader.SetFloat("interactionInputRadius", userInteractionRadius);
    }

    // 用初始数据初始化缓存
    void InitializeBufferWithData(ParticleSpawner.ParticleSpawnData spawnInfo)
    {
        float2[] positions = new float2[spawnInfo.positions.Length];
        System.Array.Copy(spawnInfo.positions, positions, spawnInfo.positions.Length);

        particlePositionsBuffer.SetData(positions);
        predictedPositionsBuffer.SetData(positions);
        particleVelocitiesBuffer.SetData(spawnInfo.velocities);
    }

    // 处理用户输入
    void ProcessUserInput()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            simulationPaused = !simulationPaused;
        }

        if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            simulationPaused = false;
            pauseAfterNextFrame = true;
        }

        if (Input.GetKeyDown(KeyCode.R))
        {
            simulationPaused = true;
            InitializeBufferWithData(initialSpawnData);
            PerformSimulationStep();
            InitializeBufferWithData(initialSpawnData);
        }
    }
}