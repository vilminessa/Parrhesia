/*
 * Parrhesia VST3 Shim — реализация C-API (см. parr_vst3.h).
 *
 * Внутри: VST3::Hosting::Module (загрузка), PlugProvider (компонента +
 * контроллер + соединения), HostProcessData (шинные буферы). Буферы
 * планарные, выделяются в Prepare; Process конвертирует interleaved↔планар.
 */
#define PARR_VST3_SHIM_EXPORTS
#include "parr_vst3.h"

#include <windows.h>

#include <public.sdk/source/vst/hosting/module.h>
#include <public.sdk/source/vst/hosting/plugprovider.h>
#include <public.sdk/source/vst/hosting/hostclasses.h>
#include <public.sdk/source/vst/hosting/processdata.h>
#include <public.sdk/source/vst/hosting/parameterchanges.h>
#include <public.sdk/source/vst/utility/uid.h>
#include <public.sdk/source/common/memorystream.h>
#include <pluginterfaces/vst/ivstaudioprocessor.h>
#include <pluginterfaces/vst/ivstcomponent.h>
#include <pluginterfaces/vst/ivsteditcontroller.h>
#include <pluginterfaces/vst/ivstprocesscontext.h>
#include <pluginterfaces/gui/iplugview.h>

#include <algorithm>
#include <atomic>
#include <cstring>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

using namespace Steinberg;

namespace
{

thread_local std::string gLastError;

// COM: каждый поток, входящий в плагин (создание/prepare/process/state),
// обязан инициализировать COM. Режим — STA (APARTMENTTHREADED): фактически
// так хост и жил (UI-поток WPF — STA); Supertone Clear в дампах крашился
// именно в MTA-потоках (T-волна). UI не меняется (STA уже → S_FALSE);
// ThreadPool/WASAPI получают STA. RAII: чей инициализировали — тем и
// снимаем; чужой режим (RPC_E_CHANGED_MODE) не трогаем.
struct ComGuard
{
    bool owned = false;

    ComGuard ()
    {
        const HRESULT hr = ::CoInitializeEx (nullptr, COINIT_APARTMENTTHREADED);
        owned = (hr == S_OK || hr == S_FALSE);
    }

    ~ComGuard ()
    {
        if (owned)
        {
            ::CoUninitialize ();
        }
    }

    ComGuard (const ComGuard&) = delete;
    ComGuard& operator= (const ComGuard&) = delete;
};

// Гонка Module::create одного модуля с разных потоков (параллельные
// enumerate/create в тестах/сканере) — сериализуем все module-пути шима.
std::mutex gModuleLock;

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
    IPlugView* view = nullptr; // редактор (owned; close до destroy)
    double sampleRate = 48000.0;
    int maxBlock = 0;
    int channels = 0;
    bool prepared = false;

    // Страховка от UB: process против prepare/destroy. Хост держит паузу
    // цепочки (SlotChainManager), шим дополнительно не даёт пересечься.
    std::atomic<int> inProcess {0};

    // Параметры: Set (main-thread) копит в SPSC-кольцо, Process (audio)
    // доставляет очередью в inputParameterChanges и чистит её.
    struct PendingParam
    {
        Vst::ParamID id;
        Vst::ParamValue value;
    };

    static constexpr int kPendingCapacity = 64;
    PendingParam pending[kPendingCapacity] {};
    std::atomic<int> pendingHead {0};
    std::atomic<int> pendingTail {0};
    Vst::ParameterChanges inChanges {32}; // host → component на ближайший process
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

// Контроллер параметров: сначала у провайдера (двухкомпонентные плагины),
// иначе сам компонент (single-component — как у редактора), с освобождением
// взятого через queryInterface reference.
struct ControllerGuard
{
    Vst::IEditController* controller = nullptr;
    bool owned = false;

    explicit ControllerGuard (Instance* instance)
    {
        if (instance == nullptr)
        {
            return;
        }

        if (instance->provider)
        {
            IPtr<Vst::IEditController> fromProvider = instance->provider->getControllerPtr ();
            if (fromProvider)
            {
                controller = fromProvider.get (); // провайдер владеет — указатель жив
                return;
            }
        }

        void* object = nullptr;
        if (instance->component &&
            instance->component->queryInterface (Vst::IEditController::iid, &object) == kResultOk &&
            object != nullptr)
        {
            controller = static_cast<Vst::IEditController*> (object);
            owned = true;
        }
    }

    ~ControllerGuard ()
    {
        if (owned && controller != nullptr)
        {
            controller->release ();
        }
    }

    ControllerGuard (const ControllerGuard&) = delete;
    ControllerGuard& operator= (const ControllerGuard&) = delete;

    explicit operator bool () const { return controller != nullptr; }
};

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

    const ComGuard comGuard;

    std::lock_guard<std::mutex> guard (gModuleLock);

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

    const ComGuard comGuard;

    std::lock_guard<std::mutex> guard (gModuleLock);

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

    const ComGuard comGuard;

    // Хост обязан снять цепочку до destroy; короткая страховка, если RT ещё
    // дорабатывает последний блок.
    {
        const auto deadline = GetTickCount64 () + 200;
        while (instance->inProcess.load (std::memory_order_acquire) != 0 &&
               GetTickCount64 () < deadline)
        {
            Sleep (1);
        }
    }

    // Редактор закрывается первым (view не должен пережить плагина).
    Pv3EditorClose (instance);

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

    const ComGuard comGuard;

    if (sampleRate <= 0.0 || maxBlockFrames <= 0 || channels != 2)
    {
        return Fail ("Pv3Prepare: нужен стерео-формат и корректный rate/block");
    }

    if (instance->inProcess.load (std::memory_order_acquire) != 0)
    {
        return Fail ("Pv3Prepare: идёт process — снимите цепочку и повторите");
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

    // RAII-счётчик: prepare/destroy видят «идёт process» и отказывают.
    struct BusyGuard
    {
        std::atomic<int>& counter;
        explicit BusyGuard (std::atomic<int>& c) : counter (c)
        {
            counter.fetch_add (1, std::memory_order_acq_rel);
        }
        ~BusyGuard () { counter.fetch_sub (1, std::memory_order_acq_rel); }
        bool Contended () const { return counter.load (std::memory_order_acquire) > 1; }
    };

    const ComGuard comGuard;
    const BusyGuard busy (instance->inProcess);
    if (busy.Contended ())
    {
        return Fail ("Pv3Process: параллельный вызов (prepare/destroy в работе)");
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

    // Доставка параметров (host → component): свежий батч из SPSC-очереди.
    instance->processData.inputParameterChanges = nullptr;
    {
        const int head = instance->pendingHead.load (std::memory_order_relaxed);
        const int tail = instance->pendingTail.load (std::memory_order_acquire);
        if (tail > head)
        {
            instance->inChanges.clearQueue ();
            for (int i = head; i < tail; i++)
            {
                const auto& change = instance->pending[i % Instance::kPendingCapacity];
                int32 queueIndex = -1;
                Vst::IParamValueQueue* queue =
                    instance->inChanges.addParameterData (change.id, queueIndex);
                if (queue != nullptr)
                {
                    int32 pointIndex = -1;
                    queue->addPoint (0, change.value, pointIndex);
                }
            }

            instance->processData.inputParameterChanges = &instance->inChanges;
            instance->pendingHead.store (tail, std::memory_order_release);
        }
    }

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

    const ComGuard comGuard;

    return static_cast<int> (instance->processor->getLatencySamples ());
}

int __cdecl Pv3GetState (void* raw, unsigned char* buffer, int capacity)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr)
    {
        return Fail ("Pv3GetState: экземпляр null");
    }

    const ComGuard comGuard;

    // Формат: [u32 compLen][component state][u32 ctrlLen][controller state].
    // Контроллер обязателен по VST3-спецификации (параметры живут в нём).
    MemoryStream componentStream;
    if (instance->component->getState (&componentStream) != kResultOk)
    {
        return Fail ("IComponent::getState не прошёл");
    }

    MemoryStream controllerStream;
    uint32_t controllerLength = 0;
    IPtr<Vst::IEditController> controller =
        instance->provider ? instance->provider->getControllerPtr () : nullptr;
    if (controller && controller->getState (&controllerStream) == kResultOk)
    {
        controllerLength = static_cast<uint32_t> (controllerStream.getSize ());
    }

    const auto componentLength = static_cast<uint32_t> (componentStream.getSize ());
    const auto total = static_cast<int> (
        sizeof (uint32_t) + componentLength + sizeof (uint32_t) + controllerLength);
    if (buffer == nullptr || capacity < total)
    {
        return total; // вызывающий узнаёт нужный размер
    }

    unsigned char* cursor = buffer;
    memcpy (cursor, &componentLength, sizeof (componentLength));
    cursor += sizeof (componentLength);
    memcpy (cursor, componentStream.getData (), componentLength);
    cursor += componentLength;
    memcpy (cursor, &controllerLength, sizeof (controllerLength));
    cursor += sizeof (controllerLength);
    if (controllerLength > 0)
    {
        memcpy (cursor, controllerStream.getData (), controllerLength);
    }

    return total;
}

int __cdecl Pv3SetState (void* raw, const unsigned char* buffer, int length)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr || buffer == nullptr || length < 0)
    {
        return Fail ("Pv3SetState: аргумент null");
    }

    const ComGuard comGuard;

    if (length < static_cast<int> (sizeof (uint32_t)))
    {
        return Fail ("Pv3SetState: буфер короче заголовка");
    }

    uint32_t componentLength = 0;
    memcpy (&componentLength, buffer, sizeof (componentLength));

    // Legacy (до нашей схемы): первые4 байта — не длина, а содержимое
    // component state — отдаём компоненту весь буфер, контроллера нет.
    const bool legacy =
        componentLength > static_cast<uint32_t> (length) ||
        sizeof (uint32_t) + static_cast<size_t> (componentLength) + sizeof (uint32_t) >
            static_cast<size_t> (length);

    const unsigned char* componentData = nullptr;
    const unsigned char* controllerData = nullptr;
    uint32_t controllerLength = 0;
    if (legacy)
    {
        componentData = buffer;
        componentLength = static_cast<uint32_t> (length);
    }
    else
    {
        componentData = buffer + sizeof (uint32_t);
        const auto* cursor = componentData + componentLength;
        memcpy (&controllerLength, cursor, sizeof (controllerLength));
        cursor += sizeof (controllerLength);
        if (sizeof (uint32_t) + static_cast<size_t> (componentLength) + sizeof (uint32_t) +
                controllerLength >
            static_cast<size_t> (length))
        {
            return Fail ("Pv3SetState: ctrlLen выходит за буфер");
        }

        controllerData = controllerLength > 0 ? cursor : nullptr;
    }

    MemoryStream componentStream (
        const_cast<unsigned char*> (componentData), static_cast<int32> (componentLength));
    if (instance->component->setState (&componentStream) != kResultOk)
    {
        return Fail ("IComponent::setState не прошёл");
    }

    if (controllerData != nullptr)
    {
        IPtr<Vst::IEditController> controller =
            instance->provider ? instance->provider->getControllerPtr () : nullptr;
        if (controller)
        {
            MemoryStream controllerStream (
                const_cast<unsigned char*> (controllerData), static_cast<int32> (controllerLength));
            controller->setState (&controllerStream); // ошибка контроллера не фатальна
        }
    }

    return 0;
}

int __cdecl Pv3ParamCount (void* raw)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr)
    {
        return Fail ("Pv3ParamCount: экземпляр null");
    }

    const ComGuard comGuard;

    LastErrorRef ().clear ();
    ControllerGuard controller (instance);
    if (!controller)
    {
        return Fail ("Pv3ParamCount: у плагина нет IEditController");
    }

    return controller.controller->getParameterCount ();
}

int __cdecl Pv3GetParamInfo (void* raw, int index, Pv3ParamInfo* out)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr || out == nullptr)
    {
        return Fail ("Pv3GetParamInfo: аргумент null");
    }

    const ComGuard comGuard;

    LastErrorRef ().clear ();
    ControllerGuard controller (instance);
    if (!controller)
    {
        return Fail ("Pv3GetParamInfo: у плагина нет IEditController");
    }

    Vst::ParameterInfo info {};
    if (controller.controller->getParameterInfo (index, info) != kResultOk)
    {
        return Fail ("Pv3ParamInfo: индекс вне диапазона");
    }

    out->id = static_cast<int> (info.id);
    out->stepCount = info.stepCount;
    out->flags = info.flags;
    out->defaultValue = info.defaultNormalizedValue;
    out->minValue = 0.0;
    out->maxValue = 1.0;

    // String128 (UTF-16) → ASCII; нелатиница — '?' (интерфейс C, буфер128).
    int32 i = 0;
    for (; i < 127; i++)
    {
        const char16 c = info.title[i];
        if (c == 0)
        {
            break;
        }

        out->name[i] = (c < 128) ? static_cast<char> (c) : '?';
    }

    out->name[i] = '\0';
    return 0;
}

int __cdecl Pv3ParamValueGet (void* raw, int id, double* outNormalized)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr || outNormalized == nullptr)
    {
        return Fail ("Pv3ParamValueGet: аргумент null");
    }

    const ComGuard comGuard;

    LastErrorRef ().clear ();
    ControllerGuard controller (instance);
    if (!controller)
    {
        return Fail ("Pv3ParamValueGet: у плагина нет IEditController");
    }

    *outNormalized = controller.controller->getParamNormalized (static_cast<Vst::ParamID> (id));
    return 0;
}

int __cdecl Pv3ParamValueSet (void* raw, int id, double normalized)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr)
    {
        return Fail ("Pv3ParamValueSet: экземпляр null");
    }

    const ComGuard comGuard;

    LastErrorRef ().clear ();
    ControllerGuard controller (instance);
    if (!controller)
    {
        return Fail ("Pv3ParamValueSet: у плагина нет IEditController");
    }

    if (!(normalized >= 0.0)) // NaN →0
    {
        normalized = 0.0;
    }
    else if (normalized > 1.0)
    {
        normalized = 1.0;
    }

    const Vst::ParamID pid = static_cast<Vst::ParamID> (id);
    controller.controller->setParamNormalized (pid, normalized); // мгновенно для чтения

    // SPSC-очередь в аудио-поток (main-thread — единственный производитель).
    const int head = instance->pendingHead.load (std::memory_order_acquire);
    const int tail = instance->pendingTail.load (std::memory_order_relaxed);
    if (tail - head < Instance::kPendingCapacity)
    {
        instance->pending[tail % Instance::kPendingCapacity] = {pid, normalized};
        instance->pendingTail.store (tail + 1, std::memory_order_release);
    }

    return 0;
}

int __cdecl Pv3EditorOpen (void* raw, void* parentHwnd)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr || parentHwnd == nullptr)
    {
        return Fail ("Pv3EditorOpen: аргумент null");
    }

    const ComGuard comGuard;

    if (instance->view != nullptr)
    {
        return 0; // идемпотентно
    }

    LastErrorRef ().clear ();

    void* controllerObject = nullptr;
    if (instance->component->queryInterface (Vst::IEditController::iid, &controllerObject) != kResultOk ||
        controllerObject == nullptr)
    {
        return Fail ("Компонент не предоставляет IEditController");
    }

    auto* controller = static_cast<Vst::IEditController*> (controllerObject);
    IPlugView* view = controller->createView (Vst::ViewType::kEditor);
    controller->release (); // view от контроллера не зависит (ref=1 у вызывающего)
    if (view == nullptr)
    {
        return Fail ("IEditController::createView вернул nullptr");
    }

    if (view->isPlatformTypeSupported (kPlatformTypeHWND) != kResultOk)
    {
        view->release ();
        return Fail ("Редактор не поддерживает HWND");
    }

    const tresult attachedResult = view->attached (parentHwnd, kPlatformTypeHWND);
    if (attachedResult != kResultOk && attachedResult != kResultTrue)
    {
        view->release ();
        return Fail ("IPlugView::attached не прошёл");
    }

    instance->view = view;
    return 0;
}

int __cdecl Pv3EditorGetSize (void* raw, int* width, int* height)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr || width == nullptr || height == nullptr)
    {
        return Fail ("Pv3EditorGetSize: аргумент null");
    }

    const ComGuard comGuard;

    if (instance->view == nullptr)
    {
        return Fail ("Pv3EditorGetSize: редактор не открыт");
    }

    ViewRect rect {};
    if (instance->view->getSize (&rect) != kResultOk)
    {
        return Fail ("IPlugView::getSize не прошёл");
    }

    *width = rect.right - rect.left;
    *height = rect.bottom - rect.top;
    return 0;
}

void __cdecl Pv3EditorClose (void* raw)
{
    auto* instance = static_cast<Instance*> (raw);
    if (instance == nullptr || instance->view == nullptr)
    {
        return;
    }

    const ComGuard comGuard;

    instance->view->removed ();
    instance->view->release ();
    instance->view = nullptr;
}

} // extern "C"
