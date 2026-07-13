# rosmsggen

Generates typed C# serializer classes from ROS 1 `.msg` files, for use with the
[Mori.RosSharp](https://www.nuget.org/packages/Mori.RosSharp) runtime.

```
dotnet tool install -g Mori.RosSharp.MsgGen

rosmsggen -o Generated -n MyApp.Ros path\to\my_msgs
```

Each message becomes a class with typed fields, a computed `RosMd5` constant (the checksum ROS
derives from the definition text, which gates every TCPROS handshake), and
`Serialize`/`Deserialize`/`ToBytes`/`FromBytes`.

## Inputs

- Single `.msg` files, or directories laid out ROS-style (`<package>/msg/<Name>.msg` or
  `<package>/<Name>.msg`) — the package name comes from the directory. Works directly against a
  ROS installation's `share` tree, including over the WSL share:

  ```
  rosmsggen -o Generated \\wsl$\Ubuntu-20.04\opt\ros\noetic\share\sensor_msgs\msg
  ```

- Nested types resolve against the inputs first, then an embedded set of the common messages
  (std_msgs, geometry_msgs, JointState, Imu, Odometry, Clock, TFMessage). The output is closed
  over dependencies, so it always compiles.

## Options

| Flag | Meaning |
|---|---|
| `-o <dir>` | output directory for the generated `.cs` files (required) |
| `-n <namespace>` | root namespace; classes land in `<namespace>.<package>` (default `RosMessages`) |
| `--package <name>` | package name for loose `.msg` files whose directory doesn't imply one |

Prefer generation at build time with no committed files? The `Mori.RosSharp` package bundles a
Roslyn source generator that does the same from `.msg` files added as `AdditionalFiles`.

## License

Apache License 2.0 © 2026 Cristian Mori.
