/*
 * Тестовые VST3-плагины Parrhesia (см. build-vst3-test-plugin.bat).
 *
 *   com.parrhesia.test.vst3.gain    — удваивает сигнал; state = u32 magic.
 *   com.parrhesia.test.vst3.latency — задержка128 кадров, getLatencySamples=128.
 *
 * Два класса в одном модуле; каждый зарегистрирован как «Audio Module Class»
 * (компонент) + «Component Controller Class» (контроллер) по образцу AGain —
 * компонент и контроллер это один и тот же класс (single-component).
 * Лицензия: MIT (как у SDK; исходник адаптирован под наши CLAP-тесты).
 */

#include <public.sdk/source/vst/vstsinglecomponenteffect.h>
#include <public.sdk/source/main/pluginfactory.h>
#include <pluginterfaces/vst/ivstaudioprocessor.h>
#include <pluginterfaces/base/ibstream.h>

#include <cstring>

using namespace Steinberg;
using namespace Steinberg::Vst;

// Уникальные class id (только для тестового модуля).
static const FUID kGainComponentUid (0x50725201, 0x4741494E, 0x434F4D50, 0x00000001);
static const FUID kGainControllerUid (0x50725201, 0x4741494E, 0x434F4D52, 0x00000001);
static const FUID kLatencyComponentUid (0x50725201, 0x4C415443, 0x434F4D50, 0x00000001);
static const FUID kLatencyControllerUid (0x50725201, 0x4C415443, 0x434F4D52, 0x00000001);

#define STATE_MAGIC 0x50525248u /* "PRRH" */
#define LATENCY_FRAMES 128
#define MAX_CHANNELS 2

namespace
{

//------------------------------------------------------------------------
// Общий предок: стерео-шина, state (u32 magic), обработка через processSample().
//------------------------------------------------------------------------
class TestEffect : public SingleComponentEffect
{
public:
    tresult PLUGIN_API initialize (FUnknown* context) SMTG_OVERRIDE
    {
        tresult result = SingleComponentEffect::initialize (context);
        addAudioInput (STR16 ("In"), SpeakerArr::kStereo);
        addAudioOutput (STR16 ("Out"), SpeakerArr::kStereo);
        m_state = STATE_MAGIC;
        return result;
    }

    tresult PLUGIN_API canProcessSampleSize (int32 symbolicSampleSize) SMTG_OVERRIDE
    {
        return symbolicSampleSize == kSample32 ? kResultOk : kResultFalse;
    }

    // Контроллер у single-component: хост запрашивает его у самого компонента,
    // отдельный class id не нужен.
    tresult PLUGIN_API getControllerClassId (TUID /*classId*/) SMTG_OVERRIDE
    {
        return kResultFalse;
    }

    tresult PLUGIN_API process (ProcessData& data) SMTG_OVERRIDE
    {
        if (data.numInputs < 1 || data.numOutputs < 1 ||
            data.inputs[0].numChannels < 1 || data.outputs[0].numChannels < 1 ||
            data.symbolicSampleSize != kSample32)
        {
            return kResultOk;
        }

        const int32 channels = data.inputs[0].numChannels;
        const int32 frames = data.numSamples;
        float* in[MAX_CHANNELS];
        float* out[MAX_CHANNELS];
        for (int32 ch = 0; ch < channels && ch < MAX_CHANNELS; ch++)
        {
            in[ch] = data.inputs[0].channelBuffers32[ch];
            out[ch] = data.outputs[0].channelBuffers32[ch];
            if (in[ch] == nullptr || out[ch] == nullptr)
            {
                return kResultOk;
            }
        }

        processAudio (in, out, channels, frames);
        // Занятые, но не использованные каналы — тишина.
        for (int32 ch = channels; ch < data.outputs[0].numChannels; ch++)
        {
            if (data.outputs[0].channelBuffers32[ch] != nullptr)
            {
                memset (data.outputs[0].channelBuffers32[ch], 0, static_cast<size_t>(frames) * sizeof (float));
            }
        }

        data.outputs[0].silenceFlags = 0;
        return kResultOk;
    }

    tresult PLUGIN_API setState (IBStream* state) SMTG_OVERRIDE
    {
        if (state == nullptr)
        {
            return kInvalidArgument;
        }

        uint32 value = 0;
        int32 read = 0;
        const tresult result = state->read (&value, sizeof (value), &read);
        if (result != kResultOk || read != static_cast<int32> (sizeof (value)))
        {
            return kResultFalse;
        }

        m_state = value;
        return kResultOk;
    }

    tresult PLUGIN_API getState (IBStream* state) SMTG_OVERRIDE
    {
        if (state == nullptr)
        {
            return kInvalidArgument;
        }

        int32 written = 0;
        return state->write (&m_state, sizeof (m_state), &written) == kResultOk &&
                       written == static_cast<int32> (sizeof (m_state))
                   ? kResultOk
                   : kResultFalse;
    }

protected:
    // Обработка блока (in-place допустим). Определяют наследники.
    virtual void processAudio (float** in, float** out, int32 channels, int32 frames) = 0;

    uint32 m_state = STATE_MAGIC;
};

//------------------------------------------------------------------------
// Gain: ×2.
//------------------------------------------------------------------------
class TestGain : public TestEffect
{
public:
    static FUnknown* createAudioInstance (void*)
    {
        return static_cast<IAudioProcessor*> (new TestGain);
    }

    static FUnknown* createControllerInstance (void*)
    {
        return static_cast<IEditController*> (new TestGain);
    }

protected:
    void processAudio (float** in, float** out, int32 channels, int32 frames) override
    {
        for (int32 ch = 0; ch < channels; ch++)
        {
            float* source = in[ch];
            float* target = out[ch];
            for (int32 i = 0; i < frames; i++)
            {
                target[i] = source[i] * 2.0f;
            }
        }
    }
};

//------------------------------------------------------------------------
// Latency: линия задержки128 кадров + getLatencySamples.
//------------------------------------------------------------------------
class TestLatency : public TestEffect
{
public:
    static FUnknown* createAudioInstance (void*)
    {
        return static_cast<IAudioProcessor*> (new TestLatency);
    }

    static FUnknown* createControllerInstance (void*)
    {
        return static_cast<IEditController*> (new TestLatency);
    }

    uint32 PLUGIN_API getLatencySamples () SMTG_OVERRIDE { return LATENCY_FRAMES; }

protected:
    void processAudio (float** in, float** out, int32 channels, int32 frames) override
    {
        for (int32 ch = 0; ch < channels && ch < MAX_CHANNELS; ch++)
        {
            float* source = in[ch];
            float* target = out[ch];
            float* ring = m_ring[ch];
            for (int32 i = 0; i < frames; i++)
            {
                const float delayed = ring[m_position];
                ring[m_position] = source[i];
                target[i] = delayed;
            }
        }

        m_position++;
        if (m_position >= LATENCY_FRAMES)
        {
            m_position = 0;
        }
    }

private:
    float m_ring[MAX_CHANNELS][LATENCY_FRAMES] {};
    int32 m_position = 0;
};

} // namespace

//------------------------------------------------------------------------
// Фабрика
//------------------------------------------------------------------------
#define stringPluginName "Parrhesia Test Gain"
#define stringPluginLatencyName "Parrhesia Test Latency"
#define stringVersion "1.0.0"
#define stringVendor "Parrhesia"

BEGIN_FACTORY_DEF (stringVendor, "https://parrhesia.local", "")

    DEF_CLASS2 (INLINE_UID_FROM_FUID (kGainComponentUid),
                PClassInfo::kManyInstances,
                kVstAudioEffectClass,
                stringPluginName,
                Vst::kDistributable,
                "Fx", /* субкатегория = эффект, попадает в сканер */
                stringVersion,
                kVstVersionString,
                TestGain::createAudioInstance)

    DEF_CLASS2 (INLINE_UID_FROM_FUID (kGainControllerUid),
                PClassInfo::kManyInstances,
                kVstComponentControllerClass,
                stringPluginName " Controller",
                0,
                "",
                stringVersion,
                kVstVersionString,
                TestGain::createControllerInstance)

    DEF_CLASS2 (INLINE_UID_FROM_FUID (kLatencyComponentUid),
                PClassInfo::kManyInstances,
                kVstAudioEffectClass,
                stringPluginLatencyName,
                Vst::kDistributable,
                "Fx",
                stringVersion,
                kVstVersionString,
                TestLatency::createAudioInstance)

    DEF_CLASS2 (INLINE_UID_FROM_FUID (kLatencyControllerUid),
                PClassInfo::kManyInstances,
                kVstComponentControllerClass,
                stringPluginLatencyName " Controller",
                0,
                "",
                stringVersion,
                kVstVersionString,
                TestLatency::createControllerInstance)

END_FACTORY
