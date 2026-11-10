using UnityEngine;

namespace GorillaDesktop
{
    // PC sesini 3B olarak çalar. Kaynak, sabit 1.0 değerli bir klip çalar; Unity bunu konuma göre
    // kısıp sağa/sola dağıtır, OnAudioFilterRead de (ses thread'inde) bu kazançları PC sesiyle çarpar.
    internal sealed class AudioPlayer : MonoBehaviour
    {
        public IAudioFeed Source;
        private int outputRate;

        public AudioSource Setup(float volume, float maxDistance)
        {
            outputRate = AudioSettings.outputSampleRate;

            var ones = new float[outputRate];
            for (var i = 0; i < ones.Length; i++) ones[i] = 1f;
            var clip = AudioClip.Create("GorillaDesktop.Ones", outputRate, 1, outputRate, false);
            clip.SetData(ones, 0);

            var source = gameObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 1.5f;
            source.maxDistance = Mathf.Max(1.6f, maxDistance);
            source.dopplerLevel = 0f;
            source.spread = 90f;
            source.volume = Mathf.Clamp01(volume);
            return source;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            var src = Source;
            if (src == null)
            {
                System.Array.Clear(data, 0, data.Length);
                return;
            }
            src.ReadInto(data, channels, outputRate);
        }
    }
}
