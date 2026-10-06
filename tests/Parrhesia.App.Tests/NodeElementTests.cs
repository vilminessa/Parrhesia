using System.Runtime.ExceptionServices;
using Parrhesia.App.Views.Graph;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Tests;

public class NodeElementTests
{
    /// <summary>
    /// WPF-элементы и их визуальное дерево живут в STA — как в реальном окне.
    /// </summary>
    private static void OnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    /// <summary>
    /// Регрессия краша07.10: пересборка порт-колонок обязана отсоединять
    /// кружки от старой панели — иначе WPF бросает InvalidOperationException
    /// («элемент уже имеет логического родителя»).
    /// </summary>
    [Fact]
    public void RebuildingPortColumns_ReparentsPortsWithoutCrash()
    {
        OnStaThread(() =>
        {
            var graph = new AudioGraph();
            var source = graph.AddNode("Вход", NodeKind.Source);
            var element = new NodeElement(source);

            // Смена режима отображения пересобирает колонки.
            element.SetViewMode(true);
            element.SetViewMode(false);
            element.SetViewMode(true);

            // Смена числа каналов (сценарий «Моно» из лога).
            graph.SetNodeChannels(source.Id, 1);
            element.RefreshFromNode();

            graph.SetNodeChannels(source.Id, 2);
            element.RefreshFromNode();

            element.SetViewMode(false);
            element.RefreshFromNode();
        });
    }

    [Fact]
    public void RefreshFromNode_KeepsPortsUsableAfterCountChange()
    {
        OnStaThread(() =>
        {
            var graph = new AudioGraph();
            var sink = graph.AddNode("Выход", NodeKind.Sink);
            var element = new NodeElement(sink);

            element.SetViewMode(true);
            graph.SetNodeChannels(sink.Id, 1, ["Монитор"]);
            element.RefreshFromNode();

            // Порты пересобраны — центры каналов должны быть валидны.
            var center = element.InputPortCenter(0);
            Assert.False(double.IsNaN(center.X));
            Assert.False(double.IsNaN(center.Y));
        });
    }
}
