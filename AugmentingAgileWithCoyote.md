---
title:  "Augmenting Agile Prorgramming with Coyote's Concurrency Testing"
---

# Augmenting Agile Prorgramming with Coyote's Concurrency Testing

Writing correct concurrent code is notoriously hard, with developers often missing a number of problematic race conditions which trigger rarely. The move to service-based architectures means developers are writing concurrent code more often that not – they have to ensure the service functions correctly no matter how many REST API calls are executing concurrently, racing and interleaving with each other.

I found out a delightfully interesting [Extreme Programming Challenge](http://wiki.c2.com/?ExtremeProgrammingChallengeFourteen) through Hillel Wayne's awesome blog post on [augmenting Agile with Formal Methods](https://www.hillelwayne.com/post/augmenting-agile/). This challenge perfectly illustrates the complexity of writing correct concurrent code.

Tom Cargill shared the following implementation of a BoundedBuffer in Java which allows readers and writers to concurrently produce and consume items from the buffer. He also mentioned there is a bug in the implementation.

How hard could it be possibly be to find that bug? Turns out, quite hard!

```java
class BoundedBuffer {

synchronized
void put(Object x) throws InterruptedException {
  while( occupied == buffer.length )
	wait();
  notify();
  ++occupied;
  putAt %= buffer.length;
  buffer[putAt++] = x;
}

synchronized
Object take() throws InterruptedException {
  while( occupied == 0 )
	wait();
  notify();
  --occupied;
  takeAt %= buffer.length;
  return buffer[takeAt++];
}

private Object[] buffer = new Object[4];
private int putAt, takeAt, occupied;
}
```

People wrote tests to thoroughly exercise the code, studied the code carefully but weren't able to figure out the bug till Tom revealed it after a couple of weeks.

Hillel in his post went on to model this problem in TLA+ and used its model checker to find the concurrency bug fairly quickly. TLA+ is an amazing tool which can find a lot of subtle issues and bugs once the system has been modeled at a sufficient level of detail. It's not working code however - it's only a model of the algorithm as Hillel points out at the end of his post.

Inspired by the post, I decided to address this challenge using [Coyote](https://microsoft.github.io/coyote/). Coyote explores the race conditions and interleavings in actual C# code and reports safety and liveness violations in your programs. If successful, the end result will not only result in Coyote finding the bug but will be an actual working implementation of BoundedBuffer.

The first challenge was transcribing the BoundedBuffer implementation in Java to C#. C# does have a `Monitor` class which be used to simulate synchronized methods and the semantics of `Notify` and `Wait` calls. In order to find deadlocks however, we had to use Coyote's `AsyncLock` implementation so Coyote could check interleavings around the _lock_ and _release_ boundaries and inform us of any deadlocks. Simulating the semantics of `synchronized`, `notify` and `wait` using the building blocks understood by Coyote was not particularly hard using Coyote-aware `AsyncLocal` and `TaskCompletionSource` implementations.

Here is the implementation of `Put` and `Take` methods:

```csharp
public async Task Put(int taskIndex, object x)
{
    await monitorLock.AcquireLock();

    while (occupied == buffer.Length)
    {
        var waitTask = Wait(taskIndex);
        monitorLock.ReleaseLock();
        await waitTask;
        await monitorLock.AcquireLock();
    }

    Notify();
    ++occupied;
    putAt %= buffer.Length;
    buffer[putAt++] = x;
    monitorLock.ReleaseLock();
}

public async Task<object> Take(int taskIndex)
{
    await monitorLock.AcquireLock();

    while (occupied == 0)
    {
        var waitTask = Wait(taskIndex);
        monitorLock.ReleaseLock();
        await waitTask;
        await monitorLock.AcquireLock();
    }

    Notify();
    --occupied;
    takeAt %= buffer.Length;
    var returnVal = buffer[takeAt++];
    monitorLock.ReleaseLock();
    return returnVal;
}
```
We use an explicit `monitorLock` object to ensure only one task is executing the `Put` and `Take` methods at any time. The `monitorLock` is released upon a `Wait` call, and reacquired before continuing on with the while loop.

We implement the `Wait` and `Notify` methods as follows:

```csharp
public Task Wait(int taskIndex)
{
    waitMap[taskIndex] = TaskCompletionSource.Create<bool>();
    return waitMap[taskIndex].Task;
}

public void Notify()
{
    if (waitMap.Count == 0)
    {
        return;
    }

    var index = randomGenerator.NextInteger(waitMap.Count);
    var taskId = waitMap.Keys.ElementAt(index);

    SignalTask(taskId);
}

private void SignalTask(int taskId)
{
    TaskCompletionSource<bool> tcs;
    waitMap.Remove(taskId, out tcs);

    tcs.SetResult(true);
}
```
The `Wait` method creates a task completion source object which suspends the task. The task completion source object is signaled in the `Notify` method, which randomly picks a waiting a task and unblocks it. It's interesting and important to note that we're using a Coyote-aware random generator object so Coyote can precisely control all sources of non-determinism in our program.

That's pretty much about it. You can find the complete source code of this implementation at [here](https://github.com/imnaseer/ExploringCoyote/tree/master/ConcurrencyXP)

It's pretty fascinating to see that we were able to transcribe Tom's code almost as-is. We just had to use Coyote-aware `Task`, `TaskCompletionSource` and `AsyncLocal` object to simulate the `synchronized` semantics in C#. Other than these minor changes, we did not have to model the algorithm in an abstract way.

Let's write a driver program now which will be run through Coyote to find the bug in the above implementation.

```csharp
public static Task Reader(int taskIndex)
{
    return Task.Run(async () =>
    {
        while (true)
        {
            object x = await boundedBuffer.Take(taskIndex);
        }
    });
}

public static Task Writer(int taskIndex)
{
    return Task.Run(async () =>
    {
        while (true)
        {
            await boundedBuffer.Put(taskIndex, new object());
        }
    });
}

[Microsoft.Coyote.SystematicTesting.Test]
public static async Task ReaderWriterTest()
{
    Console.WriteLine("Starting ReaderWriterTest");

    boundedBuffer = new BoundedBufferXP(bufferLength: 1);

    var tasks = new List<Task>();
    tasks.Add(Reader(0));
    tasks.Add(Reader(1));
    tasks.Add(Writer(2));

    await Task.WhenAll(tasks.ToArray());
}
```

We define a `Reader` task which continuously consumes items from the bounded buffer, a `Writer` task which continuously produces items into the bounded buffer. We kick off the program with two readers, one writer and a buffer length of size 1. Note that we could have used Coyote's random generator to dynamically test the split between the number of readers and writers but we chose not to do it for simplicity.

In order to make understanding the bug easier, I added a couple of log lines in the `Put` and `Take` methods.  While Coyote gives us a reproduce-able trace file which we can debug through in Visual Studio to understand the bug, it's often useful to emit these log lines as they help in understanding the bug faster.

```csharp
public async Task Put(int taskIndex, object x)
{
    await monitorLock.AcquireLock();

    Console.WriteLine($"Putting from task {taskIndex} - " + SystemSummary());

    while (occupied == buffer.Length)
    {
        var waitTask = Wait(taskIndex);
        monitorLock.ReleaseLock();
        await waitTask;
        await monitorLock.AcquireLock();
    }

    Notify();
    ++occupied;
    putAt %= buffer.Length;
    buffer[putAt++] = x;

    Console.WriteLine($"Completed Putting from task {taskIndex} - " + SystemSummary());

    monitorLock.ReleaseLock();
}
```

A similar change was made in the `Take` method.

The `SystemSummary` method is defined as follows:

```csharp
private static string SystemSummary()
{
    var waitingTasks = string.Join(", ", waitMap.Keys.ToList());
    return $"Buffer Size: {occupied}; Blocked Tasks: {{ waitingTasks }}";
}
```

Let's go ahead and run the test!

```
dotnet coyote.dll test ConcurrencyXP.dll --method ReaderWriterTest -i 10 --max-steps 100 --verbose
```

The above command asks Coyote to run the ReaderWriterTest for up to 10 iterations, with each iteration up to a 100 steps each (a _step_ is a unit of atomic execution in Coyote - you should read this article for a better understanding of steps). We're running the test for 10 iterations just for the first time. In practice, tests are often run for 10s of 1000s of iterations.

We also pass Coyote the verbose flag so we can see the console log lines in the output.

Coyote is able to find the bug in the very _first_ iteration, which points to this bug as actually being quite shallow. Let's look at the output (which I've cleaned very slightly by removing some Coyote internal logs)

```
C:\Users\imnaseer\source\repos\ConcurrencyXP\ConcurrencyXP\bin\Debug\netcoreapp2.2>dotnet coyote.dll test ConcurrencyXP.dll --method ReaderWriterTest -i 10 --max-steps 100 --verbose
. Testing ConcurrencyXP.dll
... Method ReaderWriterTest
Starting TestingProcessScheduler in process 16924
... Created '1' testing task.
... Task 0 is using 'random' strategy (seed:1379281613).
..... Iteration #1
<TestLog> Running test 'ConcurrencyXP.Program.ReaderWriterTest'.
Starting ReaderWriterTest
Taking from task 0 - Buffer Size: 0; Blocked Tasks: {  }
Taking from task 1 - Buffer Size: 0; Blocked Tasks: { 0 }
Putting from task 2 - Buffer Size: 0; Blocked Tasks: { 0, 1 }
Completed Putting from task 2 - Buffer Size: 1; Blocked Tasks: { 0 }
Putting from task 2 - Buffer Size: 1; Blocked Tasks: { 0 }
Done Taking from task 1 - Buffer Size: 0; Blocked Tasks: { 2 }
Taking from task 1 - Buffer Size: 0; Blocked Tasks: { 2 }
<ErrorLog> Deadlock detected. Task(0), Task(1), Task(3), Task(5), Task(9), Task(11) and Task(13) are waiting for a task to complete, but no other controlled tasks are enabled. Task(8), Task(10) and Task(12) are waiting to acquire a resource that is already acquired, but no other controlled tasks are enabled.
<StackTrace>    at Microsoft.Coyote.SystematicTesting.OperationScheduler.NotifyAssertionFailure(String text, Boolean killTasks, Boolean cancelExecution)
   at Microsoft.Coyote.SystematicTesting.OperationScheduler.CheckIfProgramHasDeadlocked(IEnumerable`1 ops)
   at Microsoft.Coyote.SystematicTesting.OperationScheduler.ScheduleNextEnabledOperation()
   at Microsoft.Coyote.SystematicTesting.TaskOperation.OnWaitTask(Task task)
   at Microsoft.Coyote.SystematicTesting.TaskController.<>c__DisplayClass3_0.<ScheduleAction>b__0()
   at System.Threading.ExecutionContext.RunInternal(ExecutionContext executionContext, ContextCallback callback, Object state)
   at System.Threading.Tasks.Task.ExecuteWithThreadLocal(Task& currentTaskSlot)
   at System.Threading.ThreadPoolWorkQueue.Dispatch()

<StrategyLog> Found bug using 'random' strategy.
Error: Deadlock detected. Task(0), Task(1), Task(3), Task(5), Task(9), Task(11) and Task(13) are waiting for a task to complete, but no other controlled tasks are enabled. Task(8), Task(10) and Task(12) are waiting to acquire a resource that is already acquired, but no other controlled tasks are enabled.
```

We see that Coyote found a dead-lock as it reached a state where all tasks were in the waiting state with no active task to notify any waiting task.

Can you figure out the bug from the above output? (remember tasks 0 and 1 are readers while task 2 is the writer)

Here's the sequence of events:

- The two reader tasks (`0` and `1`) run and get blocked as the buffer is empty
- The writer task `2` produces an item in the buffer, calls `Notify` and exits
` Reader `1` is awoken. Just because it is awoken does not mean it will run next; another task/thread can acquire the lock before it does
- Surely enough, this is what happens and writer task `2` acquires the lock before reader `1` and gets blocked as the buffer is full
- Reader `1` finally gets the chance to consume the item, calls `Notify` which awakes reader `0` (instead of blocked writer `2`)
- Reader `0` finds the buffer is still empty and gets blocked again; at this point reader `0` and writer `2` are both blocked
- Reader `1` runs and finds the buffer empty and blocks; all three tasks in the system are blocked and Coyote informs us of the deadlock!

This is the _exact_ same bug which Tom explained [here](http://wiki.c2.com/?ExtremeProgrammingChallengeFourteenTheBug)

The precise semantics of the program here depend on the formal Java execution semantics and the particular JVM implementation. The fact that Coyote found the exact same bug which Tom laid out gives me confidence the transcription is along the correct lines.

The discussion in the challenge pointed out that calling `NotifyAll` instead of `Notify` will fix this bug. Does it? Let's verify it using Coyote. But first we'll have to define `NotifyAll`

```csharp
public void Notify()
{
    if (waitMap.Count == 0)
    {
        return;
    }

    var index = randomGenerator.NextInteger(waitMap.Count);
    var taskId = waitMap.Keys.ElementAt(index);

    SignalTask(taskId);
}

public void NotifyAll()
{
    foreach (var key in waitMap.Keys.ToList())
    {
        SignalTask(key);
    }
}

private void SignalTask(int taskId)
{
    TaskCompletionSource<bool> tcs;
    waitMap.Remove(taskId, out tcs);

    tcs.SetResult(true);
}
```

I modified the `Put` and `Take` method to use `NotifyAll` and ran Coyote again. Sure enough, Coyote found no bugs when I ran it with 10 iterations. But does that mean there is no bug, or does it just mean there is a bug which is even rarer?

Let's run it for 1000 iterations:

```
dotnet coyote.dll test ConcurrencyXP.dll --method ReaderWriterTest -i 1000 --max-steps 100

... Method ReaderWriterTest
Starting TestingProcessScheduler in process 12156
... Created '1' testing task.
... Task 0 is using 'random' strategy (seed:1026829307).
..... Iteration 1
..... Iteration 2
..... Iteration 3
..... Iteration 4
..... Iteration 5
..... Iteration 6
..... Iteration 7
..... Iteration 8
..... Iteration 9
..... Iteration 10
..... Iteration 20
..... Iteration 30
..... Iteration 40
..... Iteration 50
..... Iteration 60
..... Iteration 70
..... Iteration 80
..... Iteration 90
..... Iteration 100
..... Iteration 200
..... Iteration 300
..... Iteration 400
..... Iteration 500
..... Iteration 600
..... Iteration 700
..... Iteration 800
..... Iteration 900
..... Iteration 1000
... Testing statstics:
..... Found 0 bugs.
... Scheduling statistics:
..... Explored 1000 schedules: 1000 fair and 0 unfair.
..... Number of scheduling points in fair terminating schedules: 1000 (min), 1000 (avg), 1000 (max).
..... Exceeded the max-steps bound of '100' in 100.00% of the fair schedules.
... Elapsed 91.950465 sec.
. Done
```

No bugs still.

This gives us confidence that the fix works. It makes sense intuitively - using `NotifyAll` instead of `Notify` would have caused the writer task `2` above to have woken up in the earlier trace, eventually filling the buffer thus resolving the deadlock.

# Lessons

Coyote was able to very quickly find a very subtle race condition - in the very first iteration in fact. I myself wasn't able to see the bug till Coyote pointed it out to me. And even then, it took me sometime to thoroughly study the log lines till I was finally able to make sense of it.

Coyote not only found the bug but it did so in an actual working C# program. Coyote's systematic exploration abilities allows teams to develop concurrent code and services in a truly agile manner, with the confidence that Coyote will find a lot of safety and liveness issues in testing instead of production.

If you're an astute reader, you may have noticed that the interleavings explored by Coyote may not have been obvious. It's only immediately clear whether Coyote explores at each possible instruction or only at certain scheduling points. It's important to develop that intuition to use Coyote effectively. I wrote a brief explanation over [here](https://imnaseer.github.io/ExploringCoyote/TaskBasedProgrammingModel.html) in order to help you develop that intuition.

Finally, you might look at this example and think it's too _academic_. After all, in real production services, you would nearly never develop an implementation of BoundedBuffer yourself and will use a tested and proven implementation from some library.

That's a fair observation.

Having said that, there are a lot of subtle race condition bugs even in simple CRUD controllers written by most developers. The REST APIs exposed by services should behave correctly even when called in a highly concurrent manner. Most developers fail to account for all the complexity there and the code often contains bugs triggered under rare or not-so-rare conditions. I organized a small [Coyote Workshop](https://github.com/imnaseer/CoyoteWorkshop) internally at Microsoft where I asked developers to write a simple CRUD controller which interacted with one or two back-end services. This is a fairly common pattern in production and almost no developer was able to write correct code right off the bat. This was a humbling realization for me and for the participants of the workshop. It underscores the need to use tools like Coyote even you're writing what may seem to you as just another simple micro-service.

Writing correct concurrent code is notoriously hard. We aren't doomed to write buggy code however. It's a sign of intelligence to recognize our intellectual limits and use systematic testing and exploration tools to augment our abilities to write reliable asynchronous software.

 Go forth and use Coyote to help catch bugs in your programs.