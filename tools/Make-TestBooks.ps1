# 生成测试 TXT 书籍（含章节结构与固定关键词）。
param(
    [string]$OutDir = 'D:\MyProjects\HiddenReader\testdata'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$chapterWords = @(
    '旧城的冬天来得早，午后四点，天色就像被谁轻轻按暗了一格。街口的梧桐只剩几片叶子，风从屋檐下穿过，带着木炭和烤栗子的气味。',
    '林间书店在巷子尽头，门面很窄，白天也开着灯。店主把最后一箱旧书搬进屋里，拍了拍封面上的灰，顺手在登记簿上写下日期。',
    '窗外的行人越走越快，只有店里的挂钟不紧不慢。程澜把新到的书按颜色排好，又觉得太整齐，反而像没有人来过。',
    '雨点先落在遮阳棚上，再落进水洼。她想起许多年前，也是这样的傍晚，父亲把伞塞给她，自己顶着报纸走进雨里。',
    '书店靠窗的位置有一个旧台灯，灯罩泛黄。常来的客人喜欢坐在那里抄书里的句子，抄完把便签留在桌角，像给下一个人留话。',
    '关门之前，程澜把那本没人买的诗集放回收银台旁边。她相信书和人都需要等待，只是等待的方式各有不同。',
    '第二天开门时，诗集被人翻开了，扉页上多了一行铅笔字：谢谢你把它留在这里。字迹很轻，像怕惊动什么。',
    '她开始留意每一位进门的人。穿灰大衣的老人、赶作业的学生、推着自行车的邮差。他们在书店里停留的样子，比在别处都要安静。',
    '冬至那天，城市提前亮起灯。书店的玻璃上蒙了一层雾，程澜用袖子擦出一小块透明，看见街上的一切都变得柔和。',
    '年底盘点，登记簿写满了两本。她把便签一张张收进铁盒，铁盒放在台灯下面，像收藏了一整年的低声交谈。',
    '春天来的时候，书店门口的柏树冒出新芽。那个留下铅笔字的人再次出现，带来一束干花和一句迟到的问候。',
    '故事讲到这里，程澜合上登记簿。窗外天色暗下来，挂钟依旧不紧不慢，书店的灯在巷子尽头亮着，等下一个推门进来的人。'
)

$sb = [System.Text.StringBuilder]::new()
$titles = @('灯下的窗户','雨停之前','旧台灯','留话的人','铅笔字','冬至','登记簿','新芽','干花','巷子尽头','玻璃上的雾','等待的方式')
for ($c = 1; $c -le 12; $c++) {
    [void]$sb.AppendLine("第${c}章　$($titles[$c-1])")
    [void]$sb.AppendLine()
    for ($p = 1; $p -le 6; $p++) {
        $w = $chapterWords[($c + $p) % $chapterWords.Count]
        [void]$sb.AppendLine($w)
        [void]$sb.AppendLine()
    }
}
$text = $sb.ToString()
$utf8Bom = New-Object System.Text.UTF8Encoding $true
[System.IO.File]::WriteAllText("$OutDir\test-book-1.txt", $text, $utf8Bom)

# 第二本：用于多书验证（简短）
$sb2 = [System.Text.StringBuilder]::new()
for ($i = 1; $i -le 3; $i++) {
    [void]$sb2.AppendLine("第${i}章　笔记")
    [void]$sb2.AppendLine()
    for ($p = 1; $p -le 4; $p++) { [void]$sb2.AppendLine('这是第二本书的正文段落，用来验证书架切换与进度隔离。梅花在墙角安静地开。'); [void]$sb2.AppendLine() }
}
[System.IO.File]::WriteAllText("$OutDir\test-book-2.txt", $sb2.ToString(), $utf8Bom)

# 第三本：无章节标题（验证无章节书仍可阅读/跳页/搜索）
$sb3 = [System.Text.StringBuilder]::new()
for ($i = 1; $i -le 30; $i++) {
    $w = $chapterWords[$i % $chapterWords.Count]
    [void]$sb3.AppendLine($w)
    [void]$sb3.AppendLine()
    if ($i -eq 15) { [void]$sb3.AppendLine('深夜的挂钟敲了十二下，巷子尽头只剩一盏灯还亮着。'); [void]$sb3.AppendLine() }
}
[System.IO.File]::WriteAllText("$OutDir\test-book-3.txt", $sb3.ToString(), $utf8Bom)

"books written: $((Get-ChildItem $OutDir).Name -join ', ')"
"book1 chars: $($text.Length)"
