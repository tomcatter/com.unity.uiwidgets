using System;
using Unity.UIWidgets.foundation;
using Unity.UIWidgets.ui;

namespace Unity.UIWidgets.gestures {
    public class DragDownDetails {
        public DragDownDetails(
            Offset globalPosition = null
        ) {
            this.globalPosition = globalPosition ?? Offset.zero;
        }

        public readonly Offset globalPosition;

        public override string ToString() {
            return this.GetType() + "(" + this.globalPosition + ")";
        }
    }

    public delegate void GestureDragDownCallback(DragDownDetails details);

    public class DragStartDetails {
        public DragStartDetails(DateTime sourceTimeStamp, Offset globalPosition = null) {
            this.sourceTimeStamp = sourceTimeStamp;
            this.globalPosition = globalPosition ?? Offset.zero;
        }

        public readonly DateTime sourceTimeStamp;

        public readonly Offset globalPosition;

        public override string ToString() {
            return this.GetType() + "(" + this.globalPosition + ")";
        }
    }

    public delegate void GestureDragStartCallback(DragStartDetails details);

    public class DragUpdateDetails {
        public DragUpdateDetails(
            DateTime sourceTimeStamp,
            Offset delta = null,
            double? primaryDelta = null,
            Offset globalPosition = null) {
            this.sourceTimeStamp = sourceTimeStamp;
            this.delta = delta ?? Offset.zero;
            this.primaryDelta = primaryDelta;
            this.globalPosition = globalPosition ?? Offset.zero;
            D.assert(primaryDelta == null
                     || primaryDelta == this.delta.dx && this.delta.dy == 0.0
                     || primaryDelta == this.delta.dy && this.delta.dx == 0.0);
        }

        public readonly DateTime sourceTimeStamp;

        public readonly Offset delta;

        public readonly double? primaryDelta;

        public readonly Offset globalPosition;

        public override string ToString() {
            return this.GetType() + "(" + this.delta + ")";
        }
    }

    public delegate void GestureDragUpdateCallback(DragUpdateDetails details);

    public class DragEndDetails {
        public DragEndDetails(
            Velocity velocity = null,
            double? primaryVelocity = null
        ) {
            this.velocity = velocity ?? Velocity.zero;
            this.primaryVelocity = primaryVelocity;

            D.assert(primaryVelocity == null
                     || primaryVelocity == this.velocity.pixelsPerSecond.dx
                     || primaryVelocity == this.velocity.pixelsPerSecond.dy);
        }

        public readonly Velocity velocity;

        public readonly double? primaryVelocity;

        public override string ToString() {
            return this.GetType() + "(" + this.velocity + ")";
        }
    }
}