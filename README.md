# 環境
- Docker
- Windows, Linux, MacOS(MacBook Air　M1での動作確認済み)

# 使い方
```
git clone https://github.com/WhiteGrouse/archive.git
cd archive

#　ここでgroupsにグループのuidを記載(複数行可)。サンプルとして以下のグループをuidを入れてある。
#　https://web.lobi.co/group/25f6117d6072df279d63da5c931a0d6224e87713

#　ここでdocker-compose.ymlのvolumesを編集して保存先を指定する。
# "- ./groups:/app/groups"の行は必須。
# 保存先は複数指定可能で、保存先のドライブは容量の最大で99%まで使う。よって同じドライブ内のディレクトリを指定するのは分散する意味がないため非推奨。
# サンプルとして実行時のカレントディレクトリにarchive1とディレクトリを作成してコンテナ内では/archive/archive1という場所にマウントするよう設定している。
# マウントした場所を自動的に検出するよう作っているのでマウント先はどこでも良い。わかりやすく/archiveの中にそれぞれのマウントポイントを設定するようにすると良い。(/mntの方がいいか？)

docker-compose up -d
```

取得速度などのログを確認する際はDocker Desktopがわかりやすいと思う。  
処理が終わるとログにMission completed...と表示されてコンテナが終了する。なおPostgreSQLは動いたまま。  

途中で中断したい場合は`docker-compose stop`で止めて、`docker-compose start`で再開できる。  
容量が足りないなどで保存先を追加したい場合は`docker-compose down`で止めて、docker-compose.ymlのvolumesを追記し、`docker-compose up -d`で再開できる。  

初期化したい場合は実行時のカレントディレクトリに生成されているdbディレクトリと保存先のディレクトリを削除する。  


# 仕様について
アーカイブはAPIのレスポンスを保存したものになる。
具体的には、1つのレスポンスあたり、バイナリでJobId(4byte), ContentLength(4byte), Contentの順で複数のレスポンスが1つのファイルにパックされて保存される。
ファイルに保存されるレスポンスの個数は1つ以上で不定だ。
また、JobIdはPostgreSQLのqueueテーブルにおけるidである。

# アクセスしているAPIエンドポイント及びパラメータ
- api/group/{group_uid}?fields=game_info,group_bookmark_info,subleaders,category,join_applications_count&count=1&members_count=0
- api/group/{group_uid}?members_cursor={cursor}&count=1&members_count=100
- api/group/{group_uid}/bookmarks?cursor={cursor}&count=100
- api/group/{group_uid}/chats?older_than={chat_uid}&count=30
- api/group/{group_uid}/chats/replies?to={chat_uid}
- api/user/{user_uid}/contacts
- api/user/{user_uid}/followers
- api/user/{user_uid}?fields=premium
