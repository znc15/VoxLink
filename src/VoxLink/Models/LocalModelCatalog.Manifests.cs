namespace VoxLink.Models;

public static partial class LocalModelCatalog
{
    private static IReadOnlyList<LocalModelArtifact> MossArtifacts() => HfManifest(
        "OpenMOSS-Team/MOSS-Transcribe-Diarize",
        "e8681d68e7042738ffca8ac8212bc8fcb1131ab8",
        """
        added_tokens.json|707|c0284b582e14987fbd3d5a2cb2bd139084371ed9acbae488829a1c900833c680
        chat_template.jinja|4_762|8641466a16b184ebaf7c4903391e607cfd532ab937e81a64120628ab79d827f4
        config.json|2_335|2b2b7a6e61334152bdd7ecf8a4da3073b4940a097e193d1d2b22093e77535234
        configuration_moss_transcribe_diarize.py|2_663|b4d12b0f4609af69b61c2fe3aa5fbaf476af22278369e4540745bc47d1d37892
        generation_config.json|107|e53a4b3ce4f944230cf1ca8fed0c42f4ff0d8c1443eaf98b5315d987334dd9e4
        merges.txt|1_671_853|8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5
        model-00000-of-00001.safetensors|1_817_113_576|9a0ceb4ab7330357db3ff583dba8d83625d5b733b00e1d55d6970e11b07026c4
        model.safetensors.index.json|65_401|0345ac5d8f360abe4e9adadb5fecd38e7730052b75f64bb58182818b1544cc36
        modeling_moss_transcribe_diarize.py|15_069|1a6f1ea11e187f04ab20d94f44c261dbfa65a17d670dcbb8b552e4a20c52877b
        preprocessor_config.json|315|ba2e601484abc80f4cded977f9a4fd4a53175b7d35c2f2511f0cfc3a32ad2499
        processing_moss_transcribe_diarize.py|10_975|6f228d22d9379e2f6a6830b18ce7336b22da8267547e96e65545d871d7f48766
        processor_config.json|292|a978c2dd54a65b576c3dae4b654fe9bcbac1184c6db2df0afb2c90fcdc872ae7
        special_tokens_map.json|613|76862e765266b85aa9459767e33cbaf13970f327a0e88d1c65846c2ddd3a1ecd
        tokenizer.json|11_423_222|bcf03774334462d6e34b5005cb11120a62275f146ee2953e68731ecdbce84fbb
        tokenizer_config.json|503|61d04c96104177240688396655ae3f7cf38ce2ea036db867a1d2b6883e27c3d5
        vocab.json|2_776_833|ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910
        """);

    private static IReadOnlyList<LocalModelArtifact> HyMtArtifacts() => HfManifest(
        "tencent/HY-MT1.5-1.8B",
        "dbad03788f49709801014c95d481a514c272ca52",
        """
        License.txt|16_270|d7d9db858500ac9073f4b5decef8e208454357226f535f65079ce4376047569f
        chat_template.jinja|654|b7491ec0e9c869dfce20f2176758099bf248d979dd05530ede99deb21698acee
        config.json|1_342|a1788df3224420f43ed1a424ad58bfacc34f689b0e477ce69d1298fa6d26292b
        generation_config.json|221|3586ba4829d9769b89523523cb562f2e894c519274f8a0e9b970287a0b1388a9
        model.safetensors|4_077_072_784|07736f560253d8c991616060fb2d855420957c268fa7d32fa8593df2f83b21ab
        special_tokens_map.json|488|bb9f59990034dae326581b9c62471523975417869f78a244b7ae2ce8cbb085eb
        tokenizer.json|9_527_287|b475bbef1b0b2fd57dcb865332b546475bd1ede2deb3bb91bafd0c047a8a530a
        tokenizer_config.json|165_815|53bd8581b601a8ee9caefeb988207de50b3fc0b733295bdf5ad68dec4cc0b07c
        """);

    private static IReadOnlyList<LocalModelArtifact> M2M100Artifacts() => HfManifest(
        "facebook/m2m100_418M",
        "55c2e61bbf05dfb8d7abccdc3fae6fc8512fd636",
        """
        config.json|908|df0ae43e4e4b0d7e3c97b7f447857a70ef6b6a2aa1f145cedbcc730d95f67134
        generation_config.json|233|aed76366507333ddbb8bd49960f23c82fe6446b3319a46a54befdb45324ccf61
        pytorch_model.bin|1_935_796_948|d907ea45e4e4b9db163382a6674f6218b3c59566fe06d77f4055c208b4e87ed1
        sentencepiece.bpe.model|2_423_393|d8f7c76ed2a5e0822be39f0a4f95a55eb19c78f4593ce609e2edbc2aea4d380a
        special_tokens_map.json|1_140|c1a4f86c3874d279ae1b2a05162858db5dd6c61665d84223ed886cbcff08fda6
        tokenizer_config.json|298|a53e6aa83da0b82565ed90c3849056307a9453843322ac5b8439ec4b9497fe48
        vocab.json|3_708_092|b6e77e474aeea8f441363aca7614317c06381f3eacfe10fb9856d5081d1074cc
        """);

    private static IReadOnlyList<LocalModelArtifact> Small100Artifacts() => HfManifest(
        "alirezamsh/small100",
        "8ab680e26a596d2e3d2d2d17ae0f68df1037328c",
        """
        config.json|890|26fd3989cea6037d432c480f5181d05c088a613394d0361edc6605a6a1058715
        model.safetensors|1_330_973_772|dd3b845a36ea4ed90437fd0b9b477e30c21f144d3658679fd5c945e3c96b0fbc
        sentencepiece.bpe.model|2_423_393|d8f7c76ed2a5e0822be39f0a4f95a55eb19c78f4593ce609e2edbc2aea4d380a
        special_tokens_map.json|1_559|009ea667e0ca903c10dac22cf7ae3a3a0b173ff33f8c64154fddd8c043805622
        tokenization_small100.py|16_019|73c8d2405e9c588582434c9f91327127faf133b9f915b203ed07ba4cd0ca9c92
        tokenizer_config.json|1_867|d5a67f279887133e8eb8b9749fd1b6b5831bb1a040fcd6ac820296041f303ba2
        vocab.json|3_708_092|b6e77e474aeea8f441363aca7614317c06381f3eacfe10fb9856d5081d1074cc
        """);

    private static IReadOnlyList<LocalModelArtifact> DotsTtsArtifacts() => HfManifest(
        "rednote-hilab/dots.tts-base",
        "12d736cb55672abe34f2e42c568647cca42c1e15",
        """
        added_tokens.json|831|1268ec3933b675b8ff4ddf688f2a6088795a122da8693645bf00a1232c4c8422
        chat_template.jinja|2_427|44d5f08f3f72b837eaad09f13a54c1f9f4eb58d75240334548b7fd52a5437fa5
        config.json|1_933|d38bd9cff56c9744e33dc0ba62ca39b1ae6ab64561628f8de8132e5b565e9fa5
        latent_stats.pt|3_197|313b13af56d659ecf869d5f854508fcf823c8f957aefc6bc05244991abd6ffe1
        llm_config.json|2_727|ce378446b2a6353547ecb44652701ec8ef42610cdcd33f728719033a9d7bb8a1
        merges.txt|1_671_853|8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5
        model.safetensors|4_396_289_197|657ea236f36d03ac86ecc82c0df1aeb368a17c5a5a7ac7369b223ff12776c8bd
        speaker_encoder.safetensors|29_150_484|1cf3861c9dee79e4db34bd0b8a4155e68bed27a7c6274e168bb6ee4fed191c85
        special_tokens_map.json|1_398|52aa5522bb1bce46f653935a856be7d0e006d1acbc881d608d57a8cb687339d7
        tokenizer.json|11_423_263|c16521f66774c7a4774e5303b7c8ec5c99830c0be5aef6c6edde3ca2a5e05dd0
        tokenizer_config.json|5_886|5fec72f1ad92d8493770ebe6fbd8bf62645f0587bfa2b6e3e12d58c766b92096
        vocab.json|2_776_833|ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910
        vocoder.safetensors|723_585_584|c0e45c08f480df67ac4c354b465355fcc7e2f6c8765263b6dfeddd1f4671c93d
        """);

    private static IReadOnlyList<LocalModelArtifact> CosyVoice2Artifacts() => HfManifest(
        "FunAudioLLM/CosyVoice2-0.5B",
        "eec1ae6c79877dbd9379285cf8789c9e0879293d",
        """
        CosyVoice-BlankEN/config.json|659|168aa1bd401abc3bc262ba15ba4e499627a8b4e006e9d050b47c22de20660185
        CosyVoice-BlankEN/generation_config.json|242|e558847a8b4402616f1273797b015104dc266fe4b520056fca88823ba8f8ebe6
        CosyVoice-BlankEN/merges.txt|1_402_109|ac8ff86a72bee70828fbc1119bc4398c6f3a9a6e490d7b0dbe917be025478bd0
        CosyVoice-BlankEN/model.safetensors|988_097_824|130282af0dfa9fe5840737cc49a0d339d06075f83c5a315c3372c9a0740d0b96
        CosyVoice-BlankEN/tokenizer_config.json|1_287|482bd979881423375ca5414e4e0d94cd7c5349dbb17fffd46b4d36d71e62a1bc
        CosyVoice-BlankEN/vocab.json|2_776_833|ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910
        campplus.onnx|28_303_423|a6ac6a63997761ae2997373e2ee1c47040854b4b759ea41ec48e4e42df0f4d73
        config.json|2|44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a
        configuration.json|47|c502b6328c67638b401df8dd05de89e9e8d1cff9cd0ada10dfbdbe13556c20de
        cosyvoice2.yaml|7_330|0af2c0d010c477187c39f3e8fd5f1ae2e4e6f90ad03ba37c10ed6c6a87b05959
        flow.decoder.estimator.fp32.onnx|286_317_026|cd54e4281701e6630730da64502d77b7e8b6e5c057cca65128bffb50f85cbf98
        flow.pt|450_575_567|ff4c2f867674411e0a08cee702996df13fa67c1cd864c06108da88d16d088541
        hift.pt|83_390_254|3386cc880324d4e98e05987b99107f49e40ed925b8ecc87c1f4939432d429879
        llm.pt|2_023_316_821|b144ef55b51ce8cfb79a73c90dbba0bdaba4e451c0ebcfab20f769264f84a608
        speech_tokenizer_v2.onnx|496_082_973|d43342aa12163a80bf07bffb94c9de2e120a8df2f9917cd2f642e7f4219c6f71
        """);

    private static IReadOnlyList<LocalModelArtifact> Qwen3TtsArtifacts() => HfManifest(
        "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
        "fd4b254389122332181a7c3db7f27e918eec64e3",
        """
        config.json|4_494|b4f01752d15a488abde3e1ab44723ae4f4b9e68a4037257b098b3737893cc1f9
        generation_config.json|245|f1b90b4513f3b34c62851049e2492d7b4c5940daf1276f89c82b8ef04127f3aa
        merges.txt|1_671_839|599bab54075088774b1733fde865d5bd747cbcc7a547c5bc12610e874e26f5e3
        model.safetensors|3_857_413_744|38fc7fc51c5e776e840414b6fd443962e9411b9654888fd7913e4da643cb857c
        preprocessor_config.json|127|efdde1022ea9d76928bf7a9cd53139138f5ba2e466e837f08f6105ab1af1c119
        speech_tokenizer/config.json|2_336|ee65bb901c876664ab8707c487157aa1a6ee57c65969b28fb5ec9dc211e68167
        speech_tokenizer/configuration.json|76|6bc26d64eb5024b4d1dab5a52371958b429256d6c9d59787f1f5294a54e0cebd
        speech_tokenizer/model.safetensors|682_293_092|836b7b357f5ea43e889936a3709af68dfe3751881acefe4ecf0dbd30ba571258
        speech_tokenizer/preprocessor_config.json|234|fcb3805e597e786d4067706e602f6688524640f8d3396790e2e09b5942fcbdfb
        tokenizer_config.json|7_344|dc3c31c3bdaedd5016382bb3cbe07323026775ad51f5a4fb564505992ae4a670
        vocab.json|2_776_833|ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910
        """);

    private static IReadOnlyList<LocalModelArtifact> HfManifest(
        string repository,
        string revision,
        string manifest) => manifest
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line =>
        {
            var fields = line.Split('|');
            var sizeText = fields.Length > 1
                ? fields[1].Replace("_", string.Empty, StringComparison.Ordinal)
                : string.Empty;
            if (fields.Length != 3 || !long.TryParse(sizeText, out var size))
            {
                throw new System.IO.InvalidDataException("本地模型工件清单格式无效。");
            }

            var path = fields[0];
            return new LocalModelArtifact(
                path,
                size,
                fields[2],
                $"https://huggingface.co/{repository}/resolve/{revision}/{path}",
                $"https://hf-mirror.com/{repository}/resolve/{revision}/{path}");
        })
        .ToArray();
}
