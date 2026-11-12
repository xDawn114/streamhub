# Go invokes AppContext/VPNService callback methods through generated bindings.
# Their Java names and callback implementations must survive release shrinking.
-keep class com.limelight.streamhub.networking.** { *; }
