# Animation queue

[Back to the README](../README.md)

The queue plays several clips one after another on a single timeline. Its main use is checking a combo: whether attack 2 reads well coming out of attack 1 at the combo window, and how far the whole string travels.

![A queue of three attacks, paused in the third](images/queue.png)

## Building a queue

Open the **Queue** tab under the viewport. Then add clips any of these ways.

- Drag clips from the animation library into the list.
- Right-click a clip in the library and choose **Add to queue**.
- Select clips and press **Add selected clips**.

Ctrl+click marks several library clips and Shift+click marks a range. Dragging one marked clip brings all of them, in library order. Dragging a folder brings every clip under it.

The list shows play order, top first. Drag a row to move it. A white outline marks the row you are dragging and a line shows where it will land. **Up**, **Down**, **Remove** and **Clear** do what they say. **Open in window** moves the queue into its own window.

Press **Play queue** to start. While the queue plays, each row also shows the clip's start and end time, how far it moves, its attack speed if it changes speed, and its hand-over point. Double-click a row to jump to it.

## Hand-over at the combo window

**Next clip starts at** is on by default. Each clip hands over to the next at an MCO annotation, the way a combo continues when the player presses attack again. The drop-down chooses `MCO_WinOpen` or `MCO_PowerWinOpen`.

- A clip without that annotation plays to its end.
- The last clip always plays to its end.
- Annotations after the hand-over are skipped, so the outgoing clip's effects and weapon events stop there.

Some clips open the window more than once. Those rows get a small drop-down right after the file name. Use it to pick which opening the hand-over uses.

## Blend

**Blend (s)** sets how long each clip blends in from the one before, with Havok's smooth ease curve. The default is 0.30 seconds. The outgoing clip keeps playing during the blend. Set it to 0 for a hard cut.

The blend changes the pose only. Root motion and annotation times stay those of the clips themselves.

## What carries through

**Root motion.** Each clip starts where the previous one left the actor, turned by every rotation so far. A string of attacks travels as far as the clips add up to, and a clip after a turn moves along the new facing.

**Attack speed.** A clip starts at the speed the previous clip ended on, since MCO only resets `MCO_AttackSpeed` to 1 outside attacks. The Configuration tab says when a clip's first speed was carried over.

The queue, its blend time and whether it was playing are saved with the session.

## What the queue does not model

The game's own transition times and curves come from the behavior graph and can differ from your blend setting. Looping the queue jumps back to the start with no blend, the same as a single looping clip.
