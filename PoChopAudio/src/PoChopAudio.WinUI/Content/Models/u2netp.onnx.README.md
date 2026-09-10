# u2netp.onnx

This file is the u2netp (u^2-net "portrait") model used by the OnnxU2Net background-removal engine.

Because the model file is ~4.4 MB and `.gitignore` excludes binary blobs, the real file is downloaded
on first build by `SCRIPTS/setup.ps1`. If you want to skip the download, the API will still start
— the O2 station will simply report the OnnxU2Net engine as unavailable until the file is present.

## Source

The model is sourced from the public [DanielGeng/U-2-Net](https://github.com/DanielGeng/U-2-Net)
release (`u2netp.pth` -> ONNX export). The downloaded file is saved as `u2netp.onnx` here.

## License

U-2-Net is published under the **Apache License 2.0**. The ONNX export is a derivative of the
trained weights, which are released under the same license. A copy of the license is included in
this directory as `LICENSE-APACHE-2.0.txt` once the model is downloaded.
