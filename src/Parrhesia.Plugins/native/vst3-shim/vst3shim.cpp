/*
 * Parrhesia VST3 Shim — реализация C-API (см. parr_vst3.h).
 *
 * Внутри: VST3::Hosting::Module (загрузка), PlugProvider (компонента +
 * контроллер + соединения), HostProcessData (шинные буферы). Буферы
 * планарные, выделяются в Prepare; Process конвертирует interleaved↔планар.
 */
#define PARR_VST3_SHIM_EXPORTS
#include "parr_vst3.h"

#include <public.sdk/source/vst/hosting/module.h>
#include <public.sdk/source/vst/hosting/plugprovider.h>
#include <public.sdk/source/vst/hosting/hostclasses.h>
#include <public.sdk/source/vst/hosting/processdata.h>
#include <public.sdk/source/vst/utility/uid.h>
#include <public.sdk/source/common/memorystream.h>
#include <pluginterfaces/vst/ivstaudioprocessor.h>
#include <pluginterfaces/vst/ivstcomponent.h>
#include <pluginterfaces/vst/ivstprocesscontext.h>

#include <algorithm>
#include <cstring>
#include <memory>
#include <optional>
#include <string>
#include <vector>

using namespace Steinberg;

namespace
{

thread_local std::string gLastError;

std::string& LastErrorRef ()
{
    return gLastError;
}

// Глобальный host-контекст (IHostApplication) — один раз на процесс.
IPtr<Vst::HostApplication> gHostApp;
std::once_flag gHostOnce;

void EnsureHostContext ()
{
    std::call_once (gHostOnce, [] () {
        gHostApp = owned (new Vst::HostApplication ());
        Vst::PluginContextFactory::instance ().setPluginContext (gHostApp.get ());
    });
}

struct Instance
{
    VST3::Hosting::Module::Ptr module;
    std::unique_ptr<Vst::PlugProvider> provider;
    IPtr<Vst::IComponent> component;        // провайдер владеет жизненным циклом
    Vst::IAudioProcessor* processor = nullptr; // ручной refcount (queryInterface)
    Vst::ProcessContext context {};
    Vst::HostProcessData processData;
    double sampleRate = 48000.0;
    int maxBlock = 0;
    int channels = 0;
    bool prepared = false;
};

Vst::IAudioProcessor* QueryProcessor (Vst::IComponent* component)
{
    void* object = nullptr;
    if (component->queryInterface (Vst::IAudioProcessor::iid, &object) != kResultOk || object == nullptr)
    {
        return nullptr;
    }

    return static_cast<Vst::IAudioProcessor*> (object);
}

int Fail (const char* message)
{
    LastErrorRef () = message;
    return -1;
}

void* FailPtr (const char* message)
{
    LastErrorRef () = message;
    return nullptr;
}

} // namespace

extern "C"
{

int __cdecl Pv3Enumerate (const char* modulePath, Pv3EnumCallback callback, void* context)
{
    if (modulePath == nullptr || callback == nullptr)
    {
        return Fail ("Pv3Enumerate: аргумент null");
    }

    std::string error;
    auto module = VST3::Hosting::Module::create (modulePath, error);
    if (!module)
    {
        return Fail (error.empty () ? "Не удалось загрузить модуль VST3" : error.c_str ());
    }

    auto& factory = module->getFactory ();
    int reported = 0;
    for (auto& info : factory.classInfos ())
    {
        if (info.category () != kVstAudioEffectClass)
        {
            continue;
        }

        const std::string cid = info.ID ().toString ();
        callback (context, cid.c_str (), info.name ().c_str (), info.vendor ().c_str ());
        reported++;
    }

    return reported;
}

const char* __cdecl Pv3LastError ()
{
    return LastErrorRef ().c_str ();
}

void* __cdecl Pv3Create (const char* modulePath, const char* classId)
{
    if (modulePath == nullptr || classId == nullptr)
    {
        return FailPtr ("Pv3Create: аргумент null");
    }

    LastErrorRef ().clear ();

    std::string error;
    auto module = VST3::Hosting::Module::create (modulePath, error);
    if (!module)
    {
        return FailPtr (error.empty () ? "Не удалось загрузить модуль VST3" : error.c_str ());
    }

    // UID::fromString ожидает32 hex-символа без дефисов — нормализуем.
    std::string cidNormalized (classId);
    cidNormalized.erase (std::remove (cidNormalized.begin (), cidNormalized.end (), '-'), cidNormalized.end ());
    auto uid = VST3::UID::fromString (cidNormalized);
    if (!uid)
    {
        return FailPtr ("Некорректный class id");
    }

    auto& factory = module->getFactory ();
    std::optional<VST3::Hosting::ClassInfo> found;
    for (auto& info : factory.classInfos ())
    {
        if (info.ID () == *uid)
        {
            found = info;
            break;
        }
    }

    if (!found)
    {
        return FailPtr ("Класс не найден в модуле");
    }

    EnsureHostContext ();
    factory.setHostContext (gHostApp.get ());

    auto instance = std::make_unique<Instance> ();
    instance->module = module;
    instance->provider = std::make_unique<Vst::PlugProvider> (factory, *found, true);
    if (!instance->provider->initialize ())
    {
        return FailPtr ("PlugProvider::initialize не прошёл (возможно, нет контроллера)");
    }

    instance->component = instance->provider->getComponentPtr ();
    if (!instance->component)
    {
        return FailPtr ("Компонент не создан");
    }

    instance->processor = QueryProcessor (instance->component);
    if (instance->processor == nullptr)
    {
        return FailPtr ("Компонент не поддерживает IAudioProcessor");
    }

    return instance.release ();
}

void __cdecl Pv3Destroy (void* raw)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr)
    {
        return;
    }

    if (instance->prepared)
    {
        if (instance->processor != nullptr)
        {
            instance->processor->setProcessing (false);
        }

        instance->component->setActive (false);
        instance->prepared = false;
    }

    instance->processData.unprepare ();

    if (instance->processor != nullptr)
    {
        instance->processor->release ();
        instance->processor = nullptr;
    }

    instance->component = nullptr;
    instance->provider.reset (); // terminate + удаление компонента
    instance->module = nullptr;
    delete instance;
}

int __cdecl Pv3Prepare (void* raw, double sampleRate, int maxBlockFrames, int channels)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr)
    {
        return Fail ("Pv3Prepare: экземпляр null");
    }

    if (sampleRate <= 0.0 || maxBlockFrames <= 0 || channels != 2)
    {
        return Fail ("Pv3Prepare: нужен стерео-формат и корректный rate/block");
    }

    LastErrorRef ().clear ();

    if (instance->prepared)
    {
        if (instance->processor->setProcessing (false) != kResultOk)
        {
            return Fail ("setProcessing(false) не прошёл");
        }

        instance->component->setActive (false);
        instance->prepared = false;
        instance->processData.unprepare ();
    }

    instance->component->setIoMode (Vst::kSimple);

    // Нужен стерео main in/out — контракт IAudioPlugin.
    Vst::SpeakerArrangement stereo = Vst::SpeakerArr::kStereo;
    if (instance->processor->setBusArrangements (&stereo, 1, &stereo, 1) != kResultOk)
    {
        return Fail ("Плагин не принимает стерео-шину main in/out");
    }

    const auto activateMain = [instance] (Vst::BusDirection direction) {
        if (instance->component->getBusCount (Vst::kAudio, direction) > 0)
        {
            instance->component->activateBus (Vst::kAudio, direction, 0, true);
        }
    };
    activateMain (Vst::kInput);
    activateMain (Vst::kOutput);

    Vst::ProcessSetup setup {Vst::kRealtime, Vst::kSample32, maxBlockFrames};
    if (instance->processor->setupProcessing (setup) != kResultOk)
    {
        return Fail ("setupProcessing не прошёл");
    }

    if (instance->component->setActive (true) != kResultOk)
    {
        return Fail ("setActive(true) не прошёл");
    }

    // Спецификация: kNotImplemented от setProcessing допустим (так делает
    // база SingleComponentEffect) — хост обязан его принимать.
    const tresult processingResult = instance->processor->setProcessing (true);
    if (processingResult != kResultOk && processingResult != kNotImplemented)
    {
        instance->component->setActive (false);
        return Fail ("setProcessing(true) не прошёл");
    }

    if (!instance->processData.prepare (*instance->component, maxBlockFrames, Vst::kSample32))
    {
        instance->processor->setProcessing (false);
        instance->component->setActive (false);
        return Fail ("HostProcessData::prepare не прошёл");
    }

    // ВАЖНО: указатели каналов НЕ трогаем — HostProcessData владеет своими
    // буферами и освобождает их через delete[] (правка указателей = крэш
    // в unprepare). Работаем с его планарными буферами напрямую.
    if (instance->processData.numInputs < 1 || instance->processData.inputs[0].numChannels < channels)
    {
        return Fail ("Нет главной входной аудио-шины");
    }

    if (instance->processData.numOutputs < 1 || instance->processData.outputs[0].numChannels < channels)
    {
        return Fail ("Нет главной выходной аудио-шины");
    }

    instance->sampleRate = sampleRate;
    instance->maxBlock = maxBlockFrames;
    instance->channels = channels;

    memset (&instance->context, 0, sizeof (instance->context));
    instance->context.sampleRate = sampleRate;
    instance->processData.processContext = &instance->context;

    instance->prepared = true;
    return 0;
}

int __cdecl Pv3Process (void* raw, float* interleaved, int frames)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr || interleaved == nullptr)
    {
        return Fail ("Pv3Process: аргумент null");
    }

    if (!instance->prepared)
    {
        return Fail ("Pv3Process: не подготовлен (Pv3Prepare)");
    }

    if (frames <= 0)
    {
        return 0;
    }

    if (frames > instance->maxBlock)
    {
        frames = instance->maxBlock; // RT-защита: без исключений
    }

    const int channels = instance->channels;
    auto& inputBus = instance->processData.inputs[0];
    auto& outputBus = instance->processData.outputs[0];

    // interleaved → планарные буферы HostProcessData (вход) + тишина на выходе.
    for (int ch = 0; ch < channels; ch++)
    {
        float* inChannel = inputBus.channelBuffers32[ch];
        float* outChannel = outputBus.channelBuffers32[ch];
        if (inChannel == nullptr || outChannel == nullptr)
        {
            return Fail ("Буфер канала не выделен");
        }

        for (int frame = 0; frame < frames; frame++)
        {
            inChannel[frame] = interleaved[frame * channels + ch];
        }

        memset (outChannel, 0, static_cast<size_t>(frames) * sizeof (float));
    }

    instance->processData.numSamples = frames;

    const tresult result = instance->processor->process (instance->processData);
    if (result != kResultOk && result != kResultTrue)
    {
        return Fail ("IAudioProcessor::process вернул ошибку");
    }

    // планарные буферы → interleaved (выход).
    for (int ch = 0; ch < channels; ch++)
    {
        const float* source = outputBus.channelBuffers32[ch];
        for (int frame = 0; frame < frames; frame++)
        {
            interleaved[frame * channels + ch] = source[frame];
        }
    }

    return 0;
}

int __cdecl Pv3GetLatency (void* raw)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr || instance->processor == nullptr)
    {
        return Fail ("Pv3GetLatency: экземпляр null");
    }

    return static_cast<int> (instance->processor->getLatencySamples ());
}

int __cdecl Pv3GetState (void* raw, unsigned char* buffer, int capacity)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr)
    {
        return Fail ("Pv3GetState: экземпляр null");
    }

    MemoryStream stream;
    if (instance->component->getState (&stream) != kResultOk)
    {
        return Fail ("IComponent::getState не прошёл");
    }

    const auto size = static_cast<int> (stream.getSize ());
    if (buffer == nullptr || capacity < size)
    {
        return size; // вызывающий узнаёт нужный размер
    }

    memcpy (buffer, stream.getData (), static_cast<size_t> (size));
    return size;
}

int __cdecl Pv3SetState (void* raw, const unsigned char* buffer, int length)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr || buffer == nullptr || length < 0)
    {
        return Fail ("Pv3SetState: аргумент null");
    }

    MemoryStream stream (const_cast<unsigned char*> (buffer), length);
    if (instance->component->setState (&stream) != kResultOk)
    {
        return Fail ("IComponent::setState не прошёл");
    }

    return 0;
}

} // extern "C"
