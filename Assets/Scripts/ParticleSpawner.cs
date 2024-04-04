using UnityEngine;
using Unity.Mathematics;

public class ParticleSpawner : MonoBehaviour
{
    public int count; // 粒子数量

    public Vector2 initialSpeed; // 初始速度
    public Vector2 center; // 生成中心点
    public Vector2 size; // 生成区域大小
    public float jitterIntensity; // 抖动强度
    public bool displaySpawnArea; // 是否显示生成区域的Gizmo
    
 // 粒子生成数据结构
    public struct ParticleSpawnData
    {
        public float2[] positions; // 位置数组
        public float2[] velocities; // 速度数组

        public ParticleSpawnData(int num)
        {
            positions = new float2[num];
            velocities = new float2[num];
        }
    }
    // 获取生成数据
    public ParticleSpawnData GetSpawnData()
    {
        ParticleSpawnData data = new ParticleSpawnData(count);
        var randomGenerator = new Unity.Mathematics.Random(42); // 随机数生成器

        for (int i = 0; i < count; i++)
        {
            // 生成在指定区域内的随机位置
            float2 position = new float2(
                center.x + (randomGenerator.NextFloat() - 0.5f) * size.x,
                center.y + (randomGenerator.NextFloat() - 0.5f) * size.y
            );

            // 计算抖动，用于在初始速度上添加随机变化
            float angle = randomGenerator.NextFloat() * Mathf.PI * 2;
            Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 jitter = direction * jitterIntensity * (randomGenerator.NextFloat() - 0.5f);

            // 设置粒子的位置和速度（加上抖动）
            data.positions[i] = position;
            data.velocities[i] = initialSpeed + jitter;
        }

        return data;
    }
}